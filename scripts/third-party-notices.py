#!/usr/bin/env python3
"""Generate THIRD-PARTY-NOTICES.md for what ships in the server image and the runner binary.

Re-run from anywhere:  python3 scripts/third-party-notices.py
Needs: web/node_modules installed (bun install), NuGet packages restored (dotnet restore), dotnet on PATH.
Exits 1 (and still writes the file) if a licence isn't known to be permissive; read the "NEEDS REVIEW" lines.
"""
import json, re, subprocess, sys, xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "web"
NUGET = Path.home() / ".nuget" / "packages"
LICENSE_FILES = ("LICENSE", "LICENSE.md", "LICENSE.txt", "LICENCE", "LICENCE.md", "license", "license.md", "license.txt",
                 "LICENSE-MIT", "LICENSE.MIT", "COPYING")
PERMISSIVE = {"MIT", "MIT-0", "ISC", "BSD-2-Clause", "BSD-3-Clause", "Apache-2.0", "0BSD", "BlueOak-1.0.0", "Unlicense",
              "CC0-1.0", "OFL-1.1", "Zlib", "Python-2.0", "CC-BY-4.0", "BSD-3-Clause-Clear"}
FONTS = ["@fontsource-variable/inter", "@fontsource-variable/jetbrains-mono"]
# brief 12: the font kit the server gives agents' pages (src/CoWork.Server/PageKit/fonts), Latin files from @fontsource 5.3.0, licences beside them
PAGE_KIT = ROOT / "src" / "CoWork.Server" / "PageKit" / "fonts"
PAGE_FONTS = [("Bricolage Grotesque", "bricolage-grotesque"), ("Fraunces", "fraunces"), ("IBM Plex Mono", "ibm-plex-mono"),
              ("IBM Plex Sans", "ibm-plex-sans"), ("Instrument Serif", "instrument-serif"), ("Source Serif 4", "source-serif-4")]
bad = []


def spdx_ok(expr):
    """True if the SPDX expression can be satisfied with permissive licences only."""
    expr = expr.strip().strip("()")
    if " OR " in expr:
        return any(spdx_ok(p) for p in re.split(r"\s+OR\s+", expr))
    if " AND " in expr:
        return all(spdx_ok(p) for p in re.split(r"\s+AND\s+", expr))
    return expr.replace("(", "").replace(")", "").strip() in PERMISSIVE


def find_license(d):
    for n in LICENSE_FILES:
        f = d / n
        if f.is_file():
            return f.read_text(errors="replace").strip()
    return None


# ---- web ----
def resolve(name, frm):
    d = frm
    while True:
        p = d / "node_modules" / name
        if (p / "package.json").is_file():
            return p
        if d == WEB.parent or d == d.parent:
            return None
        d = d.parent


def web_packages():
    root = json.loads((WEB / "package.json").read_text())
    seen, todo = {}, [(n, WEB) for n in root.get("dependencies", {})]
    while todo:
        name, frm = todo.pop()
        p = resolve(name, frm)
        if p is None:
            continue  # optional / platform-specific dependency that isn't installed here
        p = p.resolve()
        if p in seen:
            continue
        pj = json.loads((p / "package.json").read_text())
        seen[p] = pj
        deps = {**pj.get("dependencies", {}), **pj.get("optionalDependencies", {})}
        for n, _ in pj.get("peerDependencies", {}).items():
            if not pj.get("peerDependenciesMeta", {}).get(n, {}).get("optional"):
                deps.setdefault(n, "")
        todo += [(n, p) for n in deps]
    return sorted(seen.items(), key=lambda kv: (kv[1]["name"], kv[1]["version"]))


def pkg_license(pj):
    lic = pj.get("license")
    if isinstance(lic, dict):
        lic = lic.get("type")
    if not lic and pj.get("licenses"):
        lic = " OR ".join(l.get("type", "?") for l in pj["licenses"])
    return lic or "UNKNOWN"


# ---- .NET ----
def nuget_packages():
    out = {}
    for proj in ("CoWork.Server", "CoWork.Runner"):
        txt = subprocess.run(["dotnet", "list", str(ROOT / "src" / proj), "package", "--include-transitive"],
                             capture_output=True, text=True, check=True).stdout
        for m in re.finditer(r"^\s+>\s+(\S+)\s+(?:\S+\s+)?(\d[\w.\-+]*)\s*$", txt, re.M):
            out.setdefault((m.group(1), m.group(2)), set()).add(proj)
    return dict(sorted(out.items(), key=lambda kv: (kv[0][0].lower(), kv[0][1])))


def nuget_info(pid, ver):
    d = NUGET / pid.lower() / ver
    nuspec = next(d.glob("*.nuspec"), None)
    if nuspec is None:
        return "UNKNOWN", None, f"package not in {NUGET}"
    root = ET.parse(nuspec).getroot()
    ns = {"n": root.tag.split("}")[0].strip("{")}
    lic = root.find(".//n:metadata/n:license", ns)
    url = root.findtext(".//n:metadata/n:licenseUrl", namespaces=ns)
    expr, text = None, None
    if lic is not None and lic.get("type") == "expression":
        expr = (lic.text or "").strip()
    elif lic is not None and lic.get("type") == "file":
        f = d / (lic.text or "").strip()
        if f.is_file():
            text = f.read_text(errors="replace").strip()
    text = text or find_license(d)
    if not text:  # extracted folders omit the licence file; the .nupkg (a zip) carries it
        import zipfile
        nupkg = next(d.glob("*.nupkg"), None)
        if nupkg:
            with zipfile.ZipFile(nupkg) as z:
                for n in z.namelist():
                    if "/" not in n and re.match(r"(?i)licen[sc]e|copying", n):
                        text = z.read(n).decode("utf-8", "replace").strip()
                        break
    return expr, text, url


def block(title, ver, lic, text, extra=""):
    s = f"### {title} {ver}\n\nLicence: {lic}{extra}\n\n"
    if text:
        s += "```text\n" + text.replace("```", "'''") + "\n```\n\n"
    return s


def main():
    out = ["# Third-party notices\n",
           "Weave CoWork's own licences are in [NOTICE](NOTICE). This file lists the third-party software that ships in "
           "the built server image (the web app and the server's NuGet packages) and in the runner binary (its NuGet "
           "packages), with each package's licence. It is generated by `scripts/third-party-notices.py`; don't edit it by "
           "hand. The .NET runtime itself is Microsoft's, under the MIT licence, and is not listed here.\n"]

    # fonts
    out.append("## Fonts\n\nBundled with the web app (self-hosted, from the `@fontsource-variable` packages).\n")
    for n in FONTS:
        p = WEB / "node_modules" / n
        pj = json.loads((p / "package.json").read_text())
        lic = pkg_license(pj)
        text = find_license(p)
        if not spdx_ok(lic):
            bad.append(f"{n} {lic}")
        out.append(block(f"{n}", pj["version"], lic, text))

    out.append("## Fonts for agents' pages\n\nServed by the server at `/a/fonts/` for pages agents show (Latin subsets of the "
               "`@fontsource` 5.3.0 packages, in `src/CoWork.Server/PageKit/fonts`).\n")
    for name, key in PAGE_FONTS:
        out.append(block(name, "(@fontsource/" + key + " 5.3.0)", "OFL-1.1", (PAGE_KIT / "licenses" / f"{key}-OFL.txt").read_text().strip()))

    # web
    pk = web_packages()
    out.append(f"## Web app packages\n\n{len(pk)} packages: everything reachable from `dependencies` in "
               "`web/package.json`, transitive included (development tools such as Vite and Vitest are not shipped).\n")
    for p, pj in pk:
        lic = pkg_license(pj)
        text = find_license(p)
        if not spdx_ok(lic):
            bad.append(f"{pj['name']} {pj['version']}: {lic}")
        note = "" if text else "\n\n(The package ships no licence file; the licence is named in its package.json.)"
        out.append(block(pj["name"], pj["version"], lic, text, note))

    # nuget
    nu = nuget_packages()
    out.append(f"## .NET packages\n\n{len(nu)} NuGet packages of `CoWork.Server` and `CoWork.Runner`, transitive included. "
               "Where a package carries no licence text, only its SPDX licence expression is given.\n")
    for (pid, ver), projs in nu.items():
        expr, text, url = nuget_info(pid, ver)
        lic = expr or ("see licence text" if text else "UNKNOWN")
        if expr and not spdx_ok(expr):
            bad.append(f"{pid} {ver}: {expr}")
        if not expr:
            bad.append(f"NEEDS REVIEW (no SPDX expression) {pid} {ver} (licenseUrl {url})")
        out.append(block(f"{pid}", ver, lic, text, f"\n\nUsed by: {', '.join(sorted(projs))}"))

    (ROOT / "THIRD-PARTY-NOTICES.md").write_text("\n".join(out))
    print(f"wrote THIRD-PARTY-NOTICES.md: {len(FONTS)} fonts, {len(PAGE_FONTS)} page fonts, {len(pk)} web packages, {len(nu)} NuGet packages")
    for b in bad:
        print("NEEDS REVIEW:", b)
    return 1 if bad else 0


sys.exit(main())
