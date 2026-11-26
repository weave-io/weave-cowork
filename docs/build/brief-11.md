# Build brief 11: sign in with single sign-on, no passwords

The user, 26 November 2026: "When we invite people to join co-work, it seems we're forcing them to use a password … I would
have much rather preferred … OIDC … A lot of the users will very likely be using something like federated IDP like Okta or
whatever the case may be, but we should just allow standard OIDC to be used here. We should not be accepting passwords."
The plan (`mockups/cowork/plan.html`) always had OpenID Connect; brief 1 put it off and shipped passwords.

Decided with the user (mockup shown first, 26 November 2026):
- **Standard OIDC only.** Three settings: `COWORK_OIDC__AUTHORITY`, `COWORK_OIDC__CLIENTID`, `COWORK_OIDC__CLIENTSECRET`.
  No vendor named in code, settings or screens; the button says "Continue with single sign-on".
- **OIDC first; CoWork's own passkeys later, if at all.** A CoWork passkey would sign someone in without the provider, so
  disabling them in the IdP wouldn't keep them out. Providers offer passkeys on their own sign-in page.
- **No migration for password accounts.** The repository is local-only. A server that had them says so.

## The demo (the acceptance test)

1. Alex opens a fresh server, names it Lumen Labs and continues with single sign-on. The provider signs him in, he comes
   back to choose a handle and display name (suggested from the provider), and he is the admin.
2. Rosa opens Alex's invite link: "Alex invited you to Lumen Labs", one button. She signs in at the provider, keeps the
   suggested handle (a taken one says so), and is in as a member.
3. Lee, whom the provider knows but nobody invited, is told he isn't a member; "Use another account" goes back to the
   provider. The invite link is still the only way in.
4. Rosa signs out and back in and is the same member.
5. No passwords anywhere in the database. A person is the provider's issuer and subject, never the email; neither is in
   the event log.
6. A server without a provider says "Sign-in isn't set up yet" and offers no form.

## The rules every brief keeps

- Everything that exists keeps working: `dotnet test`, `bun run test`, the live checks (now signing in through a real
  provider: Dex, in podman). Add `phase15.mjs` for this demo. Screenshots in `docs/screens/brief11/`, dark and light.
