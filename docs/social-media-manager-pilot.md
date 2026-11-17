# Social Media Manager pilot

Concierge can help with many kinds of work. You can also set up a project for a specific job, such as managing social media. That focus is saved with the project, so it is there when you return. Other conversations continue as usual.

## Try it

1. Open **Projects** and choose **Social Media Manager** under Work. Create the project.
2. Review or edit the six steps: write a brief, plan content, create assets, review the work, send to SleptON!, and measure results.
3. Select **Work on this campaign** to start a conversation linked to the project.
4. Ask Concierge for a campaign brief or content calendar. It receives the project's goal and steps, along with the marketing skill, for each turn in that conversation.
5. Check the tool labels before requesting research, images, a media export, or a reminder. A listed tool may still need an account or your approval.
6. In **Social posts**, save the campaign name and audience. Add reviewed copy for each post and attach a PNG, JPEG, MP4 video, or M4A audio file if needed. You can also attach the current Creator video or sound. Visual posts need a description for accessibility. The copy and media remain local to the project; reopen the project to edit them.
7. Confirm the rights to the work and choose **Send draft to SleptON!**. Concierge makes a private `social-campaign` package with exact media hashes and no local file paths. Destination and time fields are suggestions, not posting instructions.

Publishing and ad spending are separate actions. Each needs your approval; spending also needs a connected TheGeekNetwork account and wallet, with the amount and limit shown before payment. Concierge prepares and transfers a private draft. SleptON! has locally tested an importer that accepts the exact four-post package produced by Concierge, saves it in its private queue, and rejects a tampered package. A real-device transfer and live publishing remain unverified.

## Why this shape

The project keeps the goal, editable steps, conversations, and created files together. The Social Media Manager focus adds the marketing skill and highlights relevant tools already offered by the app. Circle AI continues to choose the model.

The GitHub forks mentioned in the product discussion are research references for this pilot. No code or assets were copied from them. Each future integration needs a technical and licence review.

## Current limits

- Tool labels show which tools the app offers. They do not confirm that an account, provider, or network connection is ready.
- Direct publishing and automatic analytics are not available in Concierge. SocialMediaAPI has publishing code, but its queue stores approved post content on the server. SleptON! owns account setup, separate publish approval, per-platform results, and retry status. See the [in-house API map](integrations/in-house-api-map.md).
- SleptON!'s `social-campaign` importer and publishing journey passed local tests with Concierge's package and mocked API responses. The phone handoff and a live SocialMediaAPI post remain unverified. M4A is a private review asset, not a publishable social attachment in this pilot. Suggested posting times are not schedules.
- Other occupations need their own workflows and tool connections.
