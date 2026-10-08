# Security policy

## Reporting a vulnerability

Don't open a public issue. Report it through the **Security** tab of this repository, **Report a vulnerability**.
Include what you found, the SCSKiller version (on the About page) and the steps to reproduce. You'll get a first answer
within 7 days. Please allow reasonable time for a fix before disclosing it.

## Scope

- The app, the `scskiller` command line and the native tools (`scskiller_warm.exe`, the recorder `d3d12.dll`).
- The recorder: anything it does in a game's process, and anything written outside SCSKiller's own folders or the folder
  of a game it's installed in.
- The updater and the update feed signature: a tampered feed or package the app accepts.
- The sign-in flow and the tokens the app stores.

Only the latest stable release is supported.
