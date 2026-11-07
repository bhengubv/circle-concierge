# SolidCraft model to release

Concierge Desktop can connect to an installed SolidCraft through its local MCP server. The connection is opt-in from a Project and uses the installed `solidcraft-cli.exe`. It adds a dedicated local folder to SolidCraft's allowed file locations; SolidCraft also permits its recovery folder, temporary folder, and the open design's folder. SolidCraft tool calls use Concierge's existing approval gate. The model and exports stay on the person's device unless they choose an external action.

The **Product Model** starter Project carries one editable workflow:

1. Write the brief, including purpose, dimensions, material, tolerances and rights.
2. Model in SolidCraft and retain the editable native design.
3. Measure and inspect the model and any manufacturing export.
4. Attach the design, export, preview and listing information to the Project for review.
5. Submit through a separately configured SleptON publishing connection, then track review, feedback and revisions.

Connecting SolidCraft is **not** publishing to SleptON. Concierge has no verified model-publishing contract for SleptON in this integration. The Project records the release stage without claiming to perform it. The same brief → create → verify → package → publish → monitor pattern can serve apps and papers with their own tools and destinations.

The Windows integration uses SolidCraft's `mcp` mode, which attaches to the running design app when available and otherwise starts a headless session. The Projects screen shows the allowed model folder. An export must be attached to the Project as an artifact before it appears in the Project's output list.
