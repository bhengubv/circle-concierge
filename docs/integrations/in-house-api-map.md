# In-house API options for Concierge

Source survey: 4 November 2026. I read the local `C:\Development\repos\thegeeknetwork\code\APIs` tree: **41 API projects**, their `Program.cs` files, route/controller surfaces, and the relevant SocialMediaAPI source, tests, client library, deployment scripts, and design notes. This is a source inventory. It does not establish which APIs are deployed, healthy, or configured for a particular user. The monorepo checkout has unrelated uncommitted work, including changes in AuthAPI, SleptOnAPI, TagMeAPI, and TheJobCenterAPI; this inventory reflects that working tree.

Concierge's default is local SQLite for personal work, conversations, plans, and created content. Auth and financial services are explicit server data boundaries. Other APIs should be called for a specific operation, with the minimum data needed and a clear user action. Finding an API is not permission to copy a private workspace to it.

## Identity, trust, and money

| API | Capability visible in source | Concierge boundary |
| --- | --- | --- |
| AuthAPI | Registration, mobile login, token renewal, web approval challenges, current user, service tokens | Account identity and sign-in; server exception. |
| AccountAPI | Account endpoints and privacy operations | Account records, not a substitute for local project storage. |
| WalletAPI | Wallet views, tiers, limits, accounts, cards, and operations | Money and wallet data; server exception. |
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
| SocialMediaAPI | Account connections, queued cross-posts, per-platform results, adapters, SignalR updates | Candidate for approved publication; its current queue stores post content on the server. See findings below. |
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
| SdpktAPI | Digital retail, wallet, bounties, and related commerce | Explicit purchase or business flows. |
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

- The current SocialMediaAPI source exposes `GET /api/social/connections`, OAuth start/callback, connection revoke, `POST /api/social/shares`, share list/detail, and `/hubs/social` updates. Fourteen platform adapter classes are registered. A shared .NET client exists in `code/Shared/TheGeekNetwork.Shared.SocialMedia`, but it only queues shares.
- `social_shares` stores `content_text`, `media_urls`, hashtags, platform options, and schedule in server PostgreSQL. That is incompatible with treating Concierge's private campaign as local SQLite data. Sending an approved public post necessarily sends that post outward; it should not silently upload the campaign, drafts, or asset library.
- The OAuth callback is marked anonymous but calls `GetUserId` on the callback request. A normal provider redirect has no TGN bearer identity, so this path needs correction and an end-to-end test before Concierge relies on it.
- The worker declares retry/backoff settings, but the inspected worker uses them only in startup logging; it marks failed shares as failed rather than scheduling retries. Do not promise reliable scheduled publishing from this code yet.
- `AppInfo/APIs/SocialMediaAPI/README.md` still says “design stage” and lists planned routes that differ from the implemented routes. Source and an actual service test must govern integration. Deployment scripts exist; live service health was not tested in this survey.
- OpSupportAPI has admin campaign and funnel endpoints, but they store campaign data centrally and serve platform operations. They are not a replacement for a person's local campaign workspace or a verified source of social-platform performance data.

The next integration should start with a local campaign and a reviewed, explicit publish request. Before wiring the existing queue, decide whether SocialMediaAPI can support immediate posting with minimal server retention, and how scheduling works when the owner's devices are offline. Verify OAuth, per-platform posting, status updates, and failure recovery against a running service. Keep the original campaign, drafts, media, and measurements in Concierge's SQLite store.
