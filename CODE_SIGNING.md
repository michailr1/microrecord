# Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What is signed

Only the release binaries of MicroRecord published on the [Releases](https://github.com/michailr1/microrecord/releases) page:

- `MicroRecord-win-x64.exe` — self-contained build
- `MicroRecord-win-x64-net9.exe` — framework-dependent build

Both are built from the source code in this repository by the GitHub Actions workflow [`.github/workflows/build.yml`](.github/workflows/build.yml).
Nothing built outside that workflow is signed. Every release signing request is approved manually by an approver listed below.

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [michailr1](https://github.com/michailr1) |
| Approvers | [michailr1](https://github.com/michailr1) |

All changes reach `main` through pull requests. Code contributed by others is reviewed by a reviewer before it is merged.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

Details: recordings, settings and logs stay on the local computer. The only network connection MicroRecord makes is a local one (`127.0.0.1`) to a tab of the user's own browser, used to capture the microphone when Windows blocks direct access. Links such as "MicroRecord на GitHub" open only when the user clicks them.
