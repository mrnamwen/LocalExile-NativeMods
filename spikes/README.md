# Spike code

Experimental test code behind [the results](../07-spike-results.md). It is a rig for asking the client questions,
not the client host and not something to install. It runs on Linux under Proton only.

| File | What |
|---|---|
| `spikehost.cpp` | An `LD_PRELOAD` library for the Wine process: loads Frida Gum, runs `agent.js` once the client's image is mapped, logs what it sends, and tells it when `inbox.js` changes. Derived from LocalExile's `scripts/walkmaps/proton-recorder.cpp` |
| `agent.js` | The script inside the client: the UI functions, the text-file overlay, the chat channel, and a queue that runs inbox scripts on the main thread |
| `garden-demo.js` | An inbox script: the client half of the server-mod demo |
| `server-mod/nmspike.cs` | The server half: an ordinary LocalExile script mod (`Mods/Binaries/nmspike/nmspike.cs`) |
| `overlay-src/` | The hand-written layout, as UTF-8 for reading |
| `encode-overlay.py` | Writes `overlay-src/` as the client wants it (UTF-16LE, BOM, CRLF) |
| `runner.sh` | A `Game_CLIENT_RUNNER` wrapper that sets the preload |

## Building and running

1. In a LocalExile checkout, get the Frida GumJS devkit 17.26.2 for linux-x86_64 (`frida-gumjs.h`,
   `libfrida-gumjs.a`) from Frida's releases; LocalExile's `scripts/walkmaps/proton.py` pins its SHA-256.
2. Build, with `$T` the devkit folder:

   ```sh
   g++ -shared -fPIC -O2 spikehost.cpp -I $T -L $T -lfrida-gumjs -ldl -lm -lrt -lresolv -latomic -pthread \
       -static-libstdc++ -ffunction-sections -fdata-sections -Wl,--gc-sections -o spikehost.so
   ```
3. Put `spikehost.so`, `agent.js`, `runner.sh` and an empty `inbox.js` in one folder inside the checkout, and run
   `encode-overlay.py overlay-src <that folder>/overlay`.
4. Start the client through `live.ps1` with `Game_CLIENT_RUNNER` set to `runner.sh`. Follow LocalExile's rules for
   running the client; never start it any other way.
5. Write JavaScript to `inbox.js` (write a temporary file and rename it over). It runs on the game's main thread
   with `ctx` in scope; results go to `out.log` and `trace.log`.

## Things that will bite

- A `.ui` parse error or an id that already exists closes the client; one known layout freezes it. See the results.
- After a game exception has unwound through the script, the script's state is unreliable: restart the client.
- The addresses are the 3.29.3d client's. The agent checks only one of them against expected bytes; treat it as a
  3.29.3d-only tool.
