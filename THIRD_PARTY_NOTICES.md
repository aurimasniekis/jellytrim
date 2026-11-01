# Third-party notices

## Shipped in the release

| Component | Version | License | How |
|---|---|---|---|
| [Preact](https://preactjs.com) | 10.28.2 | MIT | Bundled into `jellytrim.js`, with its notice kept inline |

## Provided by the Jellyfin server at runtime, not redistributed

Jellyfin.Controller, Jellyfin.Model and Jellyfin.Common (GPL-3.0), Microsoft.Data.Sqlite and SQLitePCLRaw (MIT, Apache-2.0), .NET and ASP.NET Core (MIT).

## Development and test only, never shipped

xUnit, Microsoft.NET.Test.Sdk, esbuild, TypeScript, Vitest, jsdom, Testing Library, Playwright and axe-core, under their own licenses (MIT, Apache-2.0, MPL-2.0). Exact versions are pinned in `Directory.Packages.props` and in the lockfiles. The test container image `jellyfin/jellyfin` is GPL-licensed and is only pulled to run tests.
