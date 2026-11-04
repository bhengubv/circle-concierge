# Concierge Web browser journeys

Run these against a disposable Concierge Web instance with isolated personal
data. The browser suite does not launch the server or create conversations.

1. Start `Concierge.Web` in Development or run its published output at a local
   URL. Use a test profile rather than a person's everyday data directory.
2. In this folder, run `npm ci` and then set `CONCIERGE_WEB_TEST_URL` to that URL.
3. Run `npm test`.

The suite uses Chrome and checks the Desktop and phone-width Web flows. The
phone-width check is a browser viewport test, not a native Android test.
Native Android behavior is covered separately by bUnit and device acceptance.
