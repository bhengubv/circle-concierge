# Infinigen in Studio: first draft

Concierge Desktop can make one private procedural sofa draft. Install Infinigen separately, then set `CONCIERGE_INFINIGEN_EXE` to the absolute path of its `infinigen` executable before starting Concierge. Studio shows whether that executable is available. The button runs Infinigen with seed `0` and saves the result under Concierge's app-owned `content/Studio/Infinigen` folder. It does not publish to SleptON! or upload anything.

This first integration is intentionally limited to one documented Infinigen 2 object recipe. It has not yet been run with Infinigen on this machine; the installed executable, Blender environment, produced files, visual quality, 3D export, and AR use remain to be verified. Infinigen 2 is currently an alpha release. A failed run leaves `generation.log` in its draft folder for diagnosis.

References: [Infinigen 2 examples](https://infinigen.cs.princeton.edu/docs/latest/Infinigen2.html), [asset export formats](https://infinigen.cs.princeton.edu/docs/latest/ExportingToExternalFileFormats.html).
