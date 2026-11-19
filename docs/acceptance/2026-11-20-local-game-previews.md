# Local game previews — 20 November 2026

This is a development-tool and playability check, not an app-store release or a game added to Concierge. The source checkouts live outside the Concierge repository and no game code is bundled into Concierge.

## Reproduce

Clone `https://github.com/gabrielecirulli/2048` and check out commit `478b6ec346e3787f589e4af751378d06ded4cbbc`. It is static HTML, CSS, and JavaScript; no package install or compilation is required. Serve its root with `python -m http.server 8701 --bind 127.0.0.1` and open `http://127.0.0.1:8701/`.

Clone `https://github.com/Hextris/hextris` and check out commit `3f4847dc8fd7dab3d1c87e6324b9159d92fbd396`. Apply [hextris-local-preview.patch](hextris-local-preview.patch) to that clean checkout. The patch only removes automatic Google Fonts, ads, analytics, a hard-coded score request, and a dynamic remote script; it does not change gameplay. `git apply --check` against a clean checkout of that commit passed. Serve its root with `python -m http.server 8702 --bind 127.0.0.1` and open `http://127.0.0.1:8702/`. Both servers bind only to loopback.

Concierge Code's standalone host was launched separately with each source directory as its `--workspace`; both returned a version 1 ready event. Its token-protected loopback endpoint returned HTTP 302 with the token and 403 without it for the 2048 workspace. The host was stopped after the check. **Actual file editing was done by Codex in the isolated checkout, not through a Concierge Code editor action.** The games themselves required no build. This check proves the host can open these workspaces, not that Concierge's embedded editor built the games.

## Play checks

| Game | Browser result | Phone-size result | Missing check |
| --- | --- | --- | --- |
| 2048 | Local page returned 200. Arrow-key moves merged tiles and raised the score to 20. Reload restored the score and board. A separate headless Chrome run scored 4 and saved a 390 × 844 screenshot at `C:\Users\user\Documents\Codex\2026-09-24\hi\2048-phone-play.png`. | At 390 × 844, the grid and instructions rendered within the viewport. | A real touch swipe on a physical phone was not tested. |
| Hextris | Local page returned 200. Starting the game showed falling blocks; the score reached 140. Pause displayed “Game Paused.” No console error appeared in the browser check. | At 390 × 844, reloading after setting the viewport produced a centred play control and active game canvas. A second check at 320 × 740 found the pause support message overlaps the share buttons and wraps off the left edge. | Physical touch input, longer play, sound, and accessibility were not verified; the 320 px pause layout needs repair before mobile recommendation. |

The local preview URLs are temporary and work only while their loopback servers remain running on this laptop.

## Licence and asset gate

- 2048 source: MIT in `LICENSE.txt`; preserve its notice. Its bundled Clear Sans webfonts have no separate notice in that checkout. Intel's Clear Sans is Apache 2.0, but a comparison against Intel's published WOFF, EOT, and SVG files found different hashes for all nine bundled assets. The exact conversion's provenance and notice remain unresolved; replace with known licensed files or settle provenance before redistribution. The page also claims to be the “official version”; that wording must not be retained in a Concierge derivative.
- Hextris source: GPL version 3 or later in `README.md` and `LICENSE.md`. Redistributing a modified game requires the corresponding source and licence notices. Its vendored JavaScript, fonts, icons, and social assets need file-level notice review. The local patch removes automatic outbound calls, but share and store links remain user-triggered. Do not bundle it in Concierge or SleptON! from this preview alone.

Next acceptance step for a mobile-first release is physical touch play on the target phone, asset/licence cleanup, and a game-specific packaging review.
