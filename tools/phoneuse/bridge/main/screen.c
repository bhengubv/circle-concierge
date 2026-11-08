// What the bridge's screen says, redrawn by one task (SPI never runs from the
// Bluetooth or Wi-Fi callbacks, which only ask for a redraw).
#include <stdio.h>
#include <string.h>

#include "bridge.h"
#include "settings.h"
#include "display.h"
#include "font_16.h"
#include "font_22.h"
#include "font_36.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#define BG RGB(0, 0, 0)
#define FG RGB(235, 235, 235)
#define DIM RGB(140, 140, 150)
#define GOOD RGB(80, 200, 120)
#define BAD RGB(240, 90, 80)

static TaskHandle_t task;

void screen_update(void) {
    if (task) xTaskNotifyGive(task);
}

static void draw(void) {
    char a[48], b[48], c[48];
    int y = 6;
    switch (bridge.state) {
    case ST_SETUP:
        y += display_text(8, y, &font22, "PhoneUse bridge", FG, BG) + 10;
        y += display_text(8, y, &font16, "Set it up from phoneuse:", DIM, BG) + 2;
        y += display_text(8, y, &font16, "Settings > Bluetooth bridge", FG, BG) + 10;
        snprintf(a, sizeof a, "Bridge %.4s", bridge.id + 8);
        display_text(8, y, &font16, a, DIM, BG);
        break;
    case ST_CODE:
        y += display_text(8, y, &font16, "Check that phoneuse shows", DIM, BG);
        y += display_text(8, y, &font16, "the same code:", DIM, BG) + 12;
        snprintf(a, sizeof a, "%03lu %03lu", (unsigned long)(bridge.code / 1000), (unsigned long)(bridge.code % 1000));
        y += display_text(-1, y, &font36, a, FG, BG) + 8;
        display_text(8, y, &font16, "", FG, BG);
        break;
    case ST_JOINING:
        y += display_text(8, y, &font22, "Joining Wi-Fi", FG, BG) + 8;
        y += display_text(8, y, &font16, settings.ssid, DIM, BG) + 8;
        display_text(8, y, &font16, "", FG, BG);
        break;
    case ST_FAILED:
        y += display_text(8, y, &font22, "Couldn't join", BAD, BG) + 8;
        y += display_text(8, y, &font16, settings.ssid, FG, BG) + 2;
        y += display_text(8, y, &font16, bridge.err, DIM, BG) + 8;
        display_text(8, y, &font16, "Try again in phoneuse", DIM, BG);
        break;
    case ST_ONLINE: {
        // Fixed rows (135 px): network 4, phone 28, two detail rows 62 and 84,
        // radio and power 112. Nothing overlaps; screen_task clears on layout changes.
        static uint32_t last_reports;
        uint32_t rate = bridge.reports - last_reports;
        last_reports = bridge.reports;
        snprintf(a, sizeof a, "%s  %s", settings.ssid, bridge.ip);
        display_text(8, 4, &font16, a, DIM, BG);
        if (bridge.phone_connected) {
            display_text(8, 28, &font22, bridge.phone[0] ? bridge.phone : "iPhone", GOOD, BG);
            uint32_t idle = (uint32_t)(xTaskGetTickCount() * portTICK_PERIOD_MS) - bridge.last_cmd_ms;
            snprintf(b, sizeof b, "%lu reports/s, %d waiting", (unsigned long)rate, bridge.pending);
            display_text(8, 62, &font16, bridge.last_cmd_ms && idle < 5000 ? b : "Waiting for phoneuse", DIM, BG);
        } else {
            display_text(8, 28, &font22, "No phone", FG, BG);
            display_text(8, 62, &font16, "Pair on the iPhone:", DIM, BG);
            display_text(8, 84, &font16, "Bluetooth > " BRIDGE_NAME, FG, BG);
        }
        int mv = bridge.battery_mv;
        if (mv > 4400) snprintf(c, sizeof c, "Wi-Fi %d dBm   USB power", bridge.wifi_rssi);  // no battery: the charger's output
        else if (mv > 0) snprintf(c, sizeof c, "Wi-Fi %d dBm   battery %.2f V", bridge.wifi_rssi, mv / 1000.0);
        else snprintf(c, sizeof c, "Wi-Fi %d dBm", bridge.wifi_rssi);
        display_text(8, 112, &font16, c, DIM, BG);
        break;
    }
    }
}

static void screen_task(void *arg) {
    int shown = -1;
    for (;;) {
        ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(1000));  // a change, or once a second for the counters
        int layout = bridge.state * 2 + bridge.phone_connected;  // rows differ between these
        if (layout != shown) {
            display_clear(BG);
            shown = layout;
        }
        draw();
    }
}

void screen_start(void) {
    xTaskCreatePinnedToCore(screen_task, "screen", 4096, NULL, 3, &task, 0);
}
