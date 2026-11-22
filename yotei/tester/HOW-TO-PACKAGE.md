# Packaging a Ghost of Yōtei test build

These files go into the test build zip next to `SharpEmu.exe`:
`README.txt` (tester instructions) and `run-yotei.bat` (launcher).

1. `dotnet publish src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -r win-x64 --self-contained true -p:PublishDir=<folder>`
2. Copy `README.txt` and `run-yotei.bat` into `<folder>`. Put the commit hash in the README, and update its "New in this build" list and the frame rates.
3. Copy `LICENSES/GPL-3.0-or-later.txt` into `<folder>/licenses`. The PKG installer's Kraken decoder is GPL-3.0-or-later, so the distributed program is GPL-3.0.
4. Smoke-test the folder from a fresh copy (cold shader cache), then zip it.
