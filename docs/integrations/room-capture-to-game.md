# Room capture to game: first Concierge port

Concierge Creator's Space canvas can now import the measured room JSON in a
[3D Model Anything](https://github.com/wesbos/3d-model-anything) capture. This
is a local authoring path and playable browser-game draft, not a published game. The original
project's source and `scan-to-threejs` skill are MIT licensed; its example
office assets and imagery have separate terms. No upstream assets are bundled
with Concierge.

## Try it

On Desktop, open **Creator → Space** and ask Concierge to import a local 3D Model Anything
capture. `design_import_room_scan` accepts the capture ZIP, an extracted
session folder, `rooms/room-N.json`, `structure.json`, or a converted
`room.json`. For a capture with several rooms, provide `room: N` explicitly.
The source file is read in place. It is not copied to a server or embedded in
the design. One import is one undo step.

The import uses RoomPlan measurements to add editable walls, door and window
openings, a rectangular floor preview and object bounds to a new Space room. Metres in the capture
become centimetres on the canvas. The Space renderer now sizes its floor and
camera to the measured room rather than assuming every room is 4.4 metres
wide. The imported boxes are *bounds*, not detailed furniture. Inspect the
result before building interactions around it.

Once the room has at least three walls and three objects, ask Concierge to
package it as a spatial game. `design_package_spatial_game` saves a self-contained
SleptON draft ZIP in the app's `content/Exports/SleptON` folder. The browser
game lets a player find three signals by tapping glowing objects, supports
touch and pointer, and offers optional headset VR or tabletop AR on compatible
devices. The sample, tests, and receiving contract are in
`SLEPTON-SPATIAL-GAME-HANDOFF.md`.

## What the port does not claim yet

- No photo or scan mesh reconstruction, textures, lighting match, or preserved
  ARKit world map. The source capture retains those files.
- No multi-session house alignment. A ZIP containing several matching room
  files is refused rather than silently mixing coordinate systems.
- No original-room AR alignment, occlusion, physics collisions, or source-mesh
  reconstruction. Tabletop AR is a miniature model. XR hardware still needs
  device acceptance.
- SleptON does not yet import or publish `spatial-game-draft`. Concierge packages
  and verifies the ZIP; SleptON needs its own draft importer and rights gate.
- The file-path import tool is offered on Windows Desktop only. Android and iOS
  need a platform file picker for private files; hosted Web needs an upload flow.
  A Web user must never be allowed to read arbitrary server paths.

The next acceptance is a real phone AR preview and SleptON draft ingestion.
Keep the scan on the person's device; publish only a reviewed game package.
