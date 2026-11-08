// UDP control link on port 47700. phoneuse sends HID reports; the bridge answers
// each one at once with what happened, so the sender can carry skipped motion.
//
// Packet: "PU" ver(1) type(1) session(u32 LE) seq(u32 LE) body mac(8)
//   mac = HMAC-SHA256(control key, everything before it), first 8 bytes.
// Only FIND and its answer go unsigned (they carry no secrets).
//
//   0x10 FIND                 -> 0x90 {"id","name","fw","ip"}       (broadcast ok)
//   0x01 HELLO  nonce(8)      -> 0x81 nonce(8), session = a new random id
//   0x02 REPORT flags id data -> 0x82 result(0 sent, 1 skipped, 2 no phone) waiting
//        flags bit 0: skippable (motion and hovers; never keys or buttons)
//        flags bit 1: newest wins, no answer (hovers): only the newest one goes
//        to the phone, once Bluetooth has sent the previous report
//   0x03 STATUS               -> 0x83 JSON
//   0x04 CONNECT              -> 0x84 (page the last iPhone)
//   0x05 SETUP                -> 0x85 (open BLE setup again)
//   0x06 SCAN                 -> 0x86 JSON list of networks it sees (takes ~2 s)
//   0x07 JOIN nonce(12) sealed -> 0x87, then it changes network; sealed =
//        AES-GCM(HMAC(control key, "phoneuse wifi change"), "ssid\0password").
//        Back on the old network if the new one fails (STATUS "err" says why).
//   0x09 KEYS (mods, usage)*  -> 0x89 queued(u16) left(u16): typed on the bridge, in order
//   0x0A KEYSLEFT             -> 0x8A left(u16): keys not typed yet
//   0x08 TUNE what value     -> 0x88 esp_err (experiments): what 1 = coexistence
//        preference (0 Wi-Fi, 1 Bluetooth, 2 balance), 2 = Wi-Fi TX power (0.25 dBm)
// Packets after HELLO carry its session and a rising seq; an old seq is dropped,
// the same seq again gets the same answer again (a lost reply), nothing is redone.
#include <stdio.h>
#include <string.h>

#include "bridge.h"
#include "settings.h"
#include "crypto.h"
#include "esp_coexist.h"
#include "esp_log.h"
#include "esp_wifi.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "lwip/sockets.h"

#define PORT 47700
#define HDR 12
#define MAC 8

static const char *TAG = "link";
// Several programs talk to the bridge at once (the HID daemon, the page's status,
// the CLI): each HELLO gets its own session; the least recently used one goes.
#define SESSIONS 4
typedef struct {
    uint32_t sid, last_seq, last_hover, used_ms;
    uint8_t reply[600];
    int reply_len;
    struct { uint32_t seq; uint8_t len, buf[40]; } recent[6];  // short answers, for resends out of order
    uint8_t next_recent;
} session_t;

static void remember(session_t *ss, uint32_t seq, const uint8_t *b, int n) {
    memcpy(ss->reply, b, n);
    ss->reply_len = n;
    if (n <= 40) {
        int i = ss->next_recent++ % 6;
        ss->recent[i].seq = seq, ss->recent[i].len = n;
        memcpy(ss->recent[i].buf, b, n);
    }
}
static session_t sessions[SESSIONS];

static bool mac_ok(const uint8_t *b, int n) {
    uint8_t m[32];
    crypto_hmac(settings.key, b, n - MAC, m);
    uint8_t d = 0;
    for (int i = 0; i < MAC; i++) d |= m[i] ^ b[n - MAC + i];
    return d == 0;
}

static int reply(uint8_t *out, uint8_t type, uint32_t sid, uint32_t seq, const void *body, int bn, bool sign) {
    out[0] = 'P', out[1] = 'U', out[2] = 1, out[3] = type;
    memcpy(out + 4, &sid, 4);
    memcpy(out + 8, &seq, 4);
    memcpy(out + HDR, body, bn);
    int n = HDR + bn;
    if (sign) {
        uint8_t m[32];
        crypto_hmac(settings.key, out, n, m);
        memcpy(out + n, m, MAC);
        n += MAC;
    }
    return n;
}

static int status_json(char *j, int cap) {
    return snprintf(j, cap,
                    "{\"id\":\"%s\",\"fw\":\"%s\",\"phone\":\"%s\",\"connected\":%s,\"rssi\":%d,\"reports\":%lu,"
                    "\"skipped\":%lu,\"waiting\":%d,\"battery_mv\":%d,\"uptime\":%llu,\"ssid\":\"%s\","
                    "\"online\":%s,\"err\":\"%s\"}",
                    bridge.id, BRIDGE_FW, bridge.phone, bridge.phone_connected ? "true" : "false", bridge.wifi_rssi,
                    (unsigned long)bridge.reports, (unsigned long)bridge.skipped, bridge.pending, bridge.battery_mv,
                    (unsigned long long)(esp_timer_get_time() / 1000000), settings.ssid,
                    bridge.state == ST_ONLINE ? "true" : "false", bridge.err);
}

static void link_task(void *arg) {
    int s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    struct sockaddr_in a = {.sin_family = AF_INET, .sin_port = htons(PORT), .sin_addr.s_addr = htonl(INADDR_ANY)};
    bind(s, (struct sockaddr *)&a, sizeof a);
    static uint8_t in[300], out[600];
    for (;;) {
        struct sockaddr_in from;
        socklen_t fl = sizeof from;
        int n = recvfrom(s, in, sizeof in, 0, (struct sockaddr *)&from, &fl);
        if (n < HDR || in[0] != 'P' || in[1] != 'U' || in[2] != 1) continue;
        uint8_t type = in[3];
        uint32_t sid, seq;
        memcpy(&sid, in + 4, 4);
        memcpy(&seq, in + 8, 4);
        int m = 0;
        if (type == 0x10) {  // FIND
            char j[160];
            int jn = snprintf(j, sizeof j, "{\"id\":\"%s\",\"name\":\"%s\",\"fw\":\"%s\",\"ip\":\"%s\"}", bridge.id,
                              BRIDGE_NAME, BRIDGE_FW, bridge.ip);
            m = reply(out, 0x90, 0, 0, j, jn, false);
            sendto(s, out, m, 0, (struct sockaddr *)&from, fl);
            continue;
        }
        if (!settings.configured || n < HDR + MAC || !mac_ok(in, n)) continue;
        const uint8_t *body = in + HDR;
        int bn = n - HDR - MAC;
        uint32_t now_ms = esp_timer_get_time() / 1000;
        if (type == 0x01) {  // HELLO
            if (bn != 8) continue;
            session_t *v = &sessions[0];
            for (int i = 1; i < SESSIONS; i++)
                if (sessions[i].used_ms < v->used_ms) v = &sessions[i];
            crypto_random(&v->sid, 4);
            v->last_seq = v->last_hover = 0;
            v->reply_len = 0;
            memset(v->recent, 0, sizeof v->recent);
            v->used_ms = now_ms;
            m = reply(out, 0x81, v->sid, 0, body, 8, true);
            sendto(s, out, m, 0, (struct sockaddr *)&from, fl);
            continue;
        }
        session_t *ss = NULL;
        for (int i = 0; i < SESSIONS && sid; i++)
            if (sessions[i].sid == sid) ss = &sessions[i];
        if (!ss) continue;
        ss->used_ms = now_ms;
        if (type == 0x02 && bn >= 2 && (body[0] & 2)) {  // a hover: newest wins, no answer
            // Hovers keep their own order (an older one arriving late is dropped)
            // and don't move last_seq, so a resent key report still gets its answer.
            if (seq > ss->last_hover) {
                ss->last_hover = seq;
                bridge.last_cmd_ms = now_ms;
                hid_latest(body[1], body + 2, bn - 2);
            }
            continue;
        }
        if (seq == ss->last_seq && ss->reply_len) {  // the reply got lost: same answer, no redo
            sendto(s, ss->reply, ss->reply_len, 0, (struct sockaddr *)&from, fl);
            continue;
        }
        if (seq < ss->last_seq) {  // older: answer again if it was answered, never redo it
            for (int i = 0; i < 6; i++)
                if (ss->recent[i].seq == seq && ss->recent[i].len)
                    sendto(s, ss->recent[i].buf, ss->recent[i].len, 0, (struct sockaddr *)&from, fl);
            continue;
        }
        ss->last_seq = seq;
        bridge.last_cmd_ms = now_ms;
        if (type == 0x02 && bn >= 2) {  // REPORT
            uint8_t r[2];
            r[0] = hid_send(body[1], body + 2, bn - 2, body[0] & 1);
            r[1] = bridge.pending;
            m = reply(out, 0x82, sid, seq, r, 2, true);
        } else if (type == 0x03) {
            char j[400];
            m = reply(out, 0x83, sid, seq, j, status_json(j, sizeof j), true);
        } else if (type == 0x04) {
            hid_connect_last();
            m = reply(out, 0x84, sid, seq, "", 0, true);
        } else if (type == 0x06) {
            static char j[512];
            int jn = wifi_scan_json(j, sizeof j);
            m = reply(out, 0x86, sid, seq, j, jn, true);
        } else if (type == 0x07) {
            uint8_t k[32], plain[100];
            crypto_hmac(settings.key, (const uint8_t *)"phoneuse wifi change", 20, k);
            int pn = crypto_open(k, body, bn, plain, sizeof plain - 1);
            uint8_t ok = pn > 0;
            m = reply(out, 0x87, sid, seq, &ok, 1, true);
            remember(ss, seq, out, m);
            sendto(s, out, m, 0, (struct sockaddr *)&from, fl);
            if (ok) {
                plain[pn] = 0;
                const char *ssid = (char *)plain, *pass = ssid + strlen(ssid) + 1;
                if (pass > (char *)plain + pn) pass = "";
                vTaskDelay(pdMS_TO_TICKS(50));  // let the answer leave on the old network
                wifi_switch(ssid, pass);
            }
            continue;
        } else if (type == 0x09) {
            uint16_t r[2];
            r[0] = hid_keys(body, bn / 2);
            r[1] = hid_keys_left();
            m = reply(out, 0x89, sid, seq, r, 4, true);
        } else if (type == 0x0A) {
            uint16_t left = hid_keys_left();
            m = reply(out, 0x8A, sid, seq, &left, 2, true);
        } else if (type == 0x08 && bn >= 2) {
            int32_t e = -1;
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wdeprecated-declarations"
            if (body[0] == 1) e = esp_coex_preference_set((esp_coex_prefer_t)body[1]);
#pragma GCC diagnostic pop
            if (body[0] == 2) e = esp_wifi_set_max_tx_power((int8_t)body[1]);
            m = reply(out, 0x88, sid, seq, &e, 4, true);
        } else if (type == 0x05) {
            setup_ble_open(true);
            m = reply(out, 0x85, sid, seq, "", 0, true);
        } else {
            continue;
        }
        remember(ss, seq, out, m);
        sendto(s, out, m, 0, (struct sockaddr *)&from, fl);
    }
}

void link_start(void) {
    ESP_LOGI(TAG, "listening on udp %d", PORT);
    xTaskCreatePinnedToCore(link_task, "link", 6144, NULL, 18, NULL, 1);
}
