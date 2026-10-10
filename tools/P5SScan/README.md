# P5SScan (developer tool)

Research tool used to find the frame-locked counters fixed by P5SHighFps. Players do not need it.

See [docs/TECHNICAL.md](../../docs/TECHNICAL.md#research-tools-toolsp5sscan) for the commands. Scan and watch modes
only read game memory; `--trace` injects a small exception handler into the game and sets hardware breakpoints for a
few seconds, which antivirus software may flag.

Build:

```
dotnet publish -c Release -o ../../release/P5SScan
```
