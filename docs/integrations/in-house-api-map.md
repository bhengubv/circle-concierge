# In-house API options for Concierge

Source survey: 4 November 2026, with AuthAPI, SdpktAPI and SocialMediaAPI rechecked against `thegeeknetwork` `origin/master` at `97fbc429` on 5 November 2026. The original survey read the local `C:\Development\repos\thegeeknetwork\code\APIs` tree: **41 API projects**, their `Program.cs` files, route/controller surfaces, and relevant client and deployment code. This is a source inventory. It does not establish which APIs are deployed, healthy, migrated, or configured for a particular user. The monorepo checkout has unrelated uncommitted work; its local AuthAPI pairing endpoints for JobCenter and SleptOn are not part of the fetched `origin/master` contract.

Concierge's default is local SQLite for personal work, conversations, plans, and created content. Auth and financial services are explicit server data boundaries. **The wallet is a required Concierge integration for work that moves money**: purchases, paid promotion, household allowances, business expenses, and receiving revenue. Local conversation and planning must work before sign-in. For an online action, Concierge should explain that the person is signing into their TheGeekNetwork account, then ask for the action-specific approval. Other APIs should be called with the minimum data needed for that action. Finding an API is not permission to copy a private workspace to it.

## Identity, trust, and money

| API | Capability visible in source | Concierge boundary |
| --- | --- | --- |
| AuthAPI | Registration, mobile login, token renewal, web approval challenges, current user, service tokens | TheGeekNetwork account SSO for online actions, including wallet actions; separate from opening Concierge locally. |
| AccountAPI | Account endpoints and privacy operations | Account records, not a substitute for local project storage. |
| WalletAPI | Wallet views, tiers, limits, accounts, cards, and operations | Wallet balance, limits, funding, and money movement when a person chooses a financial action. |
| LedgerAPI | Balances, transaction history, transfers, reversals, fraud review, exports | Financial execution and records. |
| ShhMoneyAPI | Budgets, spending, and savings goals | Optional financial connection; do not import a user's private plans automatically. |
| PayfastAPI | Payment gateway and BRICS Pay routes | Explicit purchases and payment flows. |
| BillPaymentAPI | Bill and vehicle payment routes | Explicit payment actions. |
| ElectricityAPI | Electricity purchase routes | Explicit utility purchase actions. |
| BlockchainAPI | Tokens, wallets, purchases, rewards, and staking | Explicit asset operations. |
| TrustSealAPI | Document signing, verification, certificates, biometrics, and audit | Explicit identity or document verification. |

## Communication, social, and media

| API | Capability visible in source | Concierge boundary |
| --- | --- | --- |
| SocialMediaAPI | Account connections, queued cross-posts, per-platform results, adapters, SignalR updates | Candidate for approved publication; its queue stores the approved post on the server. See findings below. |
| MessagingAPI | Direct/group messaging, meetings, GIFs, and messaging features | A connected messaging channel; Concierge conversations stay local by default. |
| NotificationAPI | Delivery, device registration, preferences, activity feed, and status | Optional cross-device alerts with minimal content. |
| GlocellAPI | SMS, email, push, and in-app messaging | Delivery channel for explicit communication. |
| MediaAPI | Uploads, image processing, video jobs, storage, CDN, and access grants | Public or explicitly uploaded assets; private creations stay local. |
| TagMeAPI | Location-based posts, communities, events, follows, media, and sharing | Explicit social publishing or discovery. |
| WhatWeWantAPI | Stories, comments, votes, topics, trends, and preferences | Public participation and research. |
| TheHotListAPI | Charts, rankings, trending signals, and achievements | Public trend research; validate freshness before use. |
| SwearJarAPI | Moderation, blocklists, violations, and review | Optional check before public content leaves the device. |

## Commerce and distribution

| API | Capability visible in source | Concierge boundary |
| --- | --- | --- |
| SleptOnAPI | Store, creator publishing, apps, books, music, video, courses, reviews, payouts, and analytics | Explicit release or sale of a user's work. |
| KiffStoreAPI | Products, sellers, carts, orders, fulfillment, reviews, and promotions | Explicit shopping or selling. |
| EcommerceAPI | Product and order management | Commerce operations, not local project storage. |
| BidBaasAPI | Auctions, bids, auto-bids, sellers, watchlists, and advertising | Explicit marketplace actions. |
| SdpktAPI | Digital retail, wallet, bounties, and related commerce | Explicit commerce and wallet workflows; never treat a local plan as a payment instruction. |
| IncentivesAPI | Events, experience points, points, tiers, and rewards | Optional reward features. |
| SortedClothingAPI | Clothing catalogue and retail routes | Explicit product or retail work. |
| ThirdPartyAPI | Application keys, external provider connections, and OAuth integration routes | Configured external accounts; never infer a connection from source code alone. |

## Places, safety, and connectivity

| API | Capability visible in source | Concierge boundary |
| --- | --- | --- |
| CircleMapsAPI | Saved places and map preferences | Optional sync of places chosen by the user. |
| MapsAPI | Map management | Maps requested for a task. |
| MapsDataAPI | Geospatial data points | Geospatial research. |
| GeoAPI | Maps and geospatial data | Location-based analysis with location consent. |
| TakemehomeAPI | Flights, accommodation search, prices, alerts, and maps | Explicit travel planning. |
| PanikAPI | SOS, emergency contacts, trips, zones, and evidence | Emergency workflows, separate from ordinary assistant work. |
| CircleVpnAPI | VPN peers and service status | A possible secure connectivity component, subject to a separate connection design. |

## Work and platform operations

| API | Capability visible in source | Concierge boundary |
| --- | --- | --- |
| ButlerAPI | B! chat, streaming, history, analysis, translation, and model routes | Optional B! integration; do not move Concierge's private history to it by default. |
| CircleAetherAPI | Node auth, relay, channels, content, presence, SOS, and streaming routes | Connectivity and relay where needed; inspect payload and retention before use. |
| AuditAPI | Event logging, alerts, compliance reports, and proofs | Log operational events without sending private content. |
| OpSupportAPI | Admin campaigns, funnels, experiments, influencers, commissions, and analytics | Internal operations; its server campaign tables are not the user's local Social Media Manager project. |
| LocalizationAPI | Languages and translations | Translation service where local capability is insufficient. |
| TheJobCenterAPI | Jobs, applications, resumes, recruiting, alerts, and analytics | Explicit recruiter or candidate workflows. |
| WolverineAPI | Error intake, healing actions, manual queues, and approval stages | Development and operations work; retain Concierge's review and action controls. |

## Social Media Manager: what is ready in source, and what is not

- The current SocialMediaAPI source exposes `GET /api/social/connections`, `GET /api/social/connections/platforms`, OAuth authorize/callback/complete, connection revoke, `POST /api/social/shares`, share list/detail, and `/hubs/social` updates. Fourteen platform adapter classes are registered. The shared .NET client in `code/Shared/TheGeekNetwork.Shared.SocialMedia` only queues shares.
- `social_shares` stores `content_text`, `media_urls`, hashtags, platform options, and schedule in server PostgreSQL. That is incompatible with treating Concierge's private campaign as local SQLite data. Sending an approved public post necessarily sends that post outward; it should not silently upload the campaign, drafts, or asset library.
- The OAuth callback now reads the pending authorization by state without requiring a TGN bearer. The app must finish with authenticated `POST /api/social/connections/{platform}/complete`, which binds the connection to the user who started it. This corrects the earlier source-level callback defect; the full provider redirect and token exchange still need a live test.
- The worker now requeues transient per-platform failures with backoff and an attempt limit. This corrects the earlier source-level retry defect; scheduled publishing and recovery still need a live service test.
- User connections on platforms that require an app now need the person's own platform `clientId` and, where required, `clientSecret`. `GET /api/social/connections/platforms` supplies setup links and redirect URIs. Concierge must explain this setup before offering a connection and must not silently use The Geek's operator credentials for a person's account.
- `AppInfo/APIs/SocialMediaAPI/README.md` still says “design stage” and lists planned routes that differ from the implemented routes. Source and an actual service test must govern integration. Deployment scripts exist; live service health was not tested in this survey.
- OpSupportAPI has admin campaign and funnel endpoints, but they store campaign data centrally and serve platform operations. They are not a replacement for a person's local campaign workspace or a verified source of social-platform performance data.

The next integration should start with a local campaign and a reviewed, explicit publish request. Decide how much of the approved public post the service must retain and how scheduling works when the owner's devices are offline. Verify owner sign-in, the platform app setup, OAuth, posting, status updates, and failure recovery against a running service. Keep the original campaign, drafts, media, and measurements in Concierge's SQLite store.

## 5 November contract recheck

- **AuthAPI:** the fetched `origin/master` has no new Concierge-specific pairing endpoint. It already has mobile login, current-user verification, and a push-to-app web challenge. The local uncommitted JobCenter and SleptOn QR endpoints do not establish a Concierge QR sign-in contract. Concierge still needs its own owner-bound sign-in and session journey before browser or social publishing can use a TGN identity.
- **SdpktAPI:** the new commits repair payment proof concurrency, wallet history/inbox bookkeeping, and deployment-supplied ledger, bounty, stokvel, and Xero configuration. These service-side commerce fixes matter to Concierge's paid workflows. They do not move private projects server-side or make wallet sign-in a requirement for local chat.
- **SocialMediaAPI:** the OAuth and retry source defects listed in the previous survey are fixed in the fetched source, and personal platform app setup is now part of the public connection contract. No running SocialMediaAPI instance was exercised in this recheck. Concierge does not yet call it.

## Wallet actions Concierge must support

| Work in Concierge | Online action | Required user-facing gate |
| --- | --- | --- |
| Plan a project, budget, campaign, or household allowance | None | Keep the plan local; no account or wallet sign-in. |
| Buy a tool, subscription, material, or service | Checkout through the relevant commerce and wallet APIs | Show the item, payee, amount, and funding source; then ask for TheGeekNetwork account sign-in and transaction approval. |
| Run paid advertising | Platform connection, campaign publication, and payment where applicable | Separate approval for publishing from approval for the spend and its limit. |
| Pay a household allowance or business expense | Wallet transfer or payment | Show the recipient, amount, and timing; require sign-in and explicit confirmation. |
| Receive sales or other business income | Commerce payout and wallet records | Link the owner's account and show the source, status, and destination of funds. |

The app must keep an action's pending details while sign-in completes, then revalidate price, recipient, permissions, and wallet status before asking for final approval. A failed, cancelled, or expired sign-in must leave the local plan intact and must not execute the money action. The result and receipt belong to the server's financial record; Concierge may keep a local reference and status. These are implementation and acceptance requirements, **not current Concierge capabilities**. No end-to-end wallet action was verified in this survey.
