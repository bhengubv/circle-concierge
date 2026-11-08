// Wi-Fi station: join the configured network, report the address, scan for setup.
#include <stdio.h>
#include <string.h>

#include "bridge.h"
#include "settings.h"
#include "esp_event.h"
#include "esp_log.h"
#include "esp_netif.h"
#include "esp_timer.h"
#include "esp_coexist.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"

static const char *TAG = "net";
static int failures;
static bool joining_from_setup;
static bool switching;  // changing networks from phoneuse: back to the old one if this fails
static char old_ssid[33], old_pass[65];

static void connect_to(const char *ssid, const char *pass) {
    wifi_config_t w = {0};
    strncpy((char *)w.sta.ssid, ssid, sizeof w.sta.ssid);
    strncpy((char *)w.sta.password, pass, sizeof w.sta.password);
    w.sta.threshold.authmode = pass[0] ? WIFI_AUTH_WPA_PSK : WIFI_AUTH_OPEN;
    esp_wifi_set_config(WIFI_IF_STA, &w);
    esp_wifi_connect();
}
static SemaphoreHandle_t scan_done;

static void close_setup(void *arg) { setup_ble_open(false); }

static void close_setup_later(void) {
    static esp_timer_handle_t t;
    if (!t) {
        esp_timer_create_args_t a = {.callback = close_setup, .name = "setup-close"};
        esp_timer_create(&a, &t);
    }
    esp_timer_stop(t);
    esp_timer_start_once(t, 3000000);
}

static void on_event(void *arg, esp_event_base_t base, int32_t id, void *data) {
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        wifi_event_sta_disconnected_t *d = data;
        bridge.ip[0] = 0;
        if (d->reason == WIFI_REASON_ASSOC_LEAVE) return;  // we left on purpose (a switch)
        failures++;
        if (switching && failures >= 3) {
            // The new network didn't work: go back to the one that did.
            snprintf(bridge.err, sizeof bridge.err, "couldn't join %s (%s)", settings.ssid,
                     d->reason == WIFI_REASON_NO_AP_FOUND ? "not found"
                     : (d->reason == WIFI_REASON_AUTH_FAIL || d->reason == WIFI_REASON_4WAY_HANDSHAKE_TIMEOUT ||
                        d->reason == WIFI_REASON_HANDSHAKE_TIMEOUT) ? "wrong password" : "failed");
            snprintf(settings.ssid, sizeof settings.ssid, "%s", old_ssid);
            snprintf(settings.pass, sizeof settings.pass, "%s", old_pass);
            switching = false;
            failures = 0;
            bridge.state = ST_JOINING;
            screen_update();
            connect_to(settings.ssid, settings.pass);
            return;
        }
        ESP_LOGW(TAG, "disconnected, reason %d (try %d)", d->reason, failures);
        if (joining_from_setup && failures >= 3) {
            // Setup is waiting for an answer: say why, and let it try again.
            bridge.state = ST_FAILED;
            snprintf(bridge.err, sizeof bridge.err,
                     d->reason == WIFI_REASON_NO_AP_FOUND ? "network not found"
                     : (d->reason == WIFI_REASON_AUTH_FAIL || d->reason == WIFI_REASON_4WAY_HANDSHAKE_TIMEOUT ||
                        d->reason == WIFI_REASON_HANDSHAKE_TIMEOUT)
                         ? "wrong password"
                         : "could not join (reason %d)",
                     d->reason);
            joining_from_setup = false;
            setup_ble_notify_info();
            screen_update();
            return;
        }
        if (bridge.state == ST_ONLINE) bridge.state = ST_JOINING;
        screen_update();
        esp_wifi_connect();  // a saved network: keep trying
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_SCAN_DONE) {
        if (scan_done) xSemaphoreGive(scan_done);
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        ip_event_got_ip_t *e = data;
        snprintf(bridge.ip, sizeof bridge.ip, IPSTR, IP2STR(&e->ip_info.ip));
        failures = 0;
        if (switching) {
            switching = false;
            settings_save();  // the new network works: keep it (the control key stays)
        } else if (!bridge.err[0] || joining_from_setup) {
            bridge.err[0] = 0;
        }
        if (joining_from_setup) {
            settings_save();  // the network works: keep it, and the control key
            joining_from_setup = false;
            close_setup_later();  // phoneuse reads the address over BLE first
        }
        bridge.state = ST_ONLINE;
        ESP_LOGI(TAG, "online at %s", bridge.ip);
        setup_ble_notify_info();
        screen_update();
    }
}

void wifi_init(void) {
    esp_netif_init();
    esp_event_loop_create_default();
    esp_netif_create_default_wifi_sta();
    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&cfg));
    esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, on_event, NULL);
    esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, on_event, NULL);
    esp_wifi_set_storage(WIFI_STORAGE_RAM);
    esp_wifi_set_mode(WIFI_MODE_STA);
    ESP_ERROR_CHECK(esp_wifi_start());
    // Commands arrive over Wi-Fi: a sleeping radio would hold them until its next
    // wake-up. Ask for no power save; with Bluetooth on, the coexistence layer may
    // refuse, which the log shows.
    esp_err_t e = esp_wifi_set_ps(WIFI_PS_NONE);
    // Commands come over Wi-Fi and the Bluetooth traffic is a trickle of small
    // reports: give Wi-Fi the larger share of the radio.
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wdeprecated-declarations"
    ESP_LOGI(TAG, "prefer Wi-Fi: %s", esp_err_to_name(esp_coex_preference_set(ESP_COEX_PREFER_WIFI)));
#pragma GCC diagnostic pop
    ESP_LOGI(TAG, "power save off: %s", esp_err_to_name(e));
    if (settings.configured) {
        wifi_config_t w = {0};
        strncpy((char *)w.sta.ssid, settings.ssid, sizeof w.sta.ssid);
        strncpy((char *)w.sta.password, settings.pass, sizeof w.sta.password);
        esp_wifi_set_config(WIFI_IF_STA, &w);
        bridge.state = ST_JOINING;
        esp_wifi_connect();
    }
}

void wifi_join(const char *ssid, const char *pass) {
    wifi_config_t w = {0};
    strncpy((char *)w.sta.ssid, ssid, sizeof w.sta.ssid);
    strncpy((char *)w.sta.password, pass, sizeof w.sta.password);
    w.sta.threshold.authmode = pass[0] ? WIFI_AUTH_WPA_PSK : WIFI_AUTH_OPEN;
    esp_wifi_disconnect();
    esp_wifi_set_config(WIFI_IF_STA, &w);
    failures = 0;
    joining_from_setup = true;
    bridge.state = ST_JOINING;
    bridge.err[0] = 0;
    setup_ble_notify_info();
    screen_update();
    esp_wifi_connect();
}

void wifi_switch(const char *ssid, const char *pass) {
    snprintf(old_ssid, sizeof old_ssid, "%s", settings.ssid);
    snprintf(old_pass, sizeof old_pass, "%s", settings.pass);
    snprintf(settings.ssid, sizeof settings.ssid, "%s", ssid);
    snprintf(settings.pass, sizeof settings.pass, "%s", pass);
    switching = true;
    failures = 0;
    bridge.err[0] = 0;
    bridge.state = ST_JOINING;
    screen_update();
    esp_wifi_disconnect();
    connect_to(ssid, pass);
}

static void json_str(char **p, char *end, const char *s) {
    // ssids are bytes: escape quotes, backslashes and control characters
    if (*p < end) *(*p)++ = '"';
    for (; *s && *p < end - 7; s++) {
        unsigned char c = *s;
        if (c == '"' || c == '\\') *p += snprintf(*p, end - *p, "\\%c", c);
        else if (c < 32) *p += snprintf(*p, end - *p, "\\u%04x", c);
        else *(*p)++ = c;
    }
    if (*p < end) *(*p)++ = '"';
}

int wifi_scan_json(char *out, int cap) {
    if (!scan_done) scan_done = xSemaphoreCreateBinary();
    wifi_scan_config_t sc = {.show_hidden = false};
    if (esp_wifi_scan_start(&sc, false) != ESP_OK) return snprintf(out, cap, "[]");
    xSemaphoreTake(scan_done, pdMS_TO_TICKS(6000));
    uint16_t n = 20;
    wifi_ap_record_t recs[20];
    esp_wifi_scan_get_ap_records(&n, recs);
    char *p = out, *end = out + cap - 2;
    *p++ = '[';
    int written = 0;
    for (int i = 0; i < n && p < end - 60; i++) {
        if (!recs[i].ssid[0]) continue;
        bool dup = false;  // one entry per name (strongest first: the list is sorted)
        for (int j = 0; j < i; j++) dup |= !strcmp((char *)recs[j].ssid, (char *)recs[i].ssid);
        if (dup) continue;
        if (written++) *p++ = ',';
        p += snprintf(p, end - p, "{\"s\":");
        json_str(&p, end, (char *)recs[i].ssid);
        p += snprintf(p, end - p, ",\"r\":%d,\"l\":%d}", recs[i].rssi, recs[i].authmode != WIFI_AUTH_OPEN);
    }
    *p++ = ']';
    *p = 0;
    return p - out;
}
