// Code-behind for BusinessApis. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;


namespace Concierge.Shared.Components.Pages;

public partial class BusinessApis
{

    private static readonly (string Name, string Examples, string Summary)[] Groups =
    [
        ("Social and messaging", "Meta, WhatsApp, X, LinkedIn, Kakao, WeChat", "Reach customers and route conversations into support, sales, and campaigns."),
        ("Commerce", "Shopify, WooCommerce, AliExpress, Taobao", "Keep storefronts, marketplace actions, products, and orders connected."),
        ("Finance", "Xero, Stripe, PayPal, bank feeds", "Track money, invoices, payments, reconciliation, and evidence."),
        ("Logistics", "DHL, FedEx, Parcel Ninja, local couriers", "Turn orders into delivery workflows and customer updates."),
        ("Productivity", "Microsoft Graph, Google Workspace, Slack, Notion", "Keep files, notes, meetings, and operating rhythm connected.")
    ];
}
