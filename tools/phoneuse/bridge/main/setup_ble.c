// Setup over BLE: phoneuse on the PC finds the bridge, exchanges keys, checks the
// code with the person, lists Wi-Fi networks through the bridge, and sends the
// chosen network's password encrypted. One GATT service:
//
//   INFO  read/notify  JSON: id, firmware, state, ssid, ip, error
//   KEX   read/write   read: the bridge's X25519 public key; write: the PC's
//   SCAN  read/write   write anything: scan; read: JSON list (or "scanning")
//   JOIN  write        AES-GCM(wifi key, "ssid\0password"): join that network
#include <stdio.h>
#include <string.h>

#include "bridge.h"
#include "settings.h"
#include "crypto.h"
#include "esp_bt.h"
#include "esp_gap_ble_api.h"
#include "esp_gatt_common_api.h"
#include "esp_gatts_api.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "setup";

// c0de00xx-7068-6f6e-6575-736562726467 ("phoneusebrdg"), little-endian for Bluedroid.
#define UUID(n) {0x67, 0x64, 0x72, 0x62, 0x65, 0x73, 0x75, 0x65, 0x6e, 0x6f, 0x68, 0x70, n, 0x00, 0xde, 0xc0}
static const uint8_t U_SVC[16] = UUID(0x01), U_INFO[16] = UUID(0x02), U_KEX[16] = UUID(0x03),
                     U_SCAN[16] = UUID(0x04), U_JOIN[16] = UUID(0x05);

enum { A_SVC, A_INFO_C, A_INFO_V, A_INFO_CCC, A_KEX_C, A_KEX_V, A_SCAN_C, A_SCAN_V, A_JOIN_C, A_JOIN_V, A_N };
static uint16_t handle[A_N];
static esp_gatt_if_t gif = ESP_GATT_IF_NONE;
static uint16_t conn = 0xFFFF;

static uint8_t our_pub[32], wifi_key[32], ctrl_key[32];
static bool keyed;

static const uint16_t PRIMARY = ESP_GATT_UUID_PRI_SERVICE, CHAR_DECL = ESP_GATT_UUID_CHAR_DECLARE,
                      CCC = ESP_GATT_UUID_CHAR_CLIENT_CONFIG;
static const uint8_t P_READ_NOTIFY = ESP_GATT_CHAR_PROP_BIT_READ | ESP_GATT_CHAR_PROP_BIT_NOTIFY,
                     P_READ_WRITE = ESP_GATT_CHAR_PROP_BIT_READ | ESP_GATT_CHAR_PROP_BIT_WRITE,
                     P_WRITE = ESP_GATT_CHAR_PROP_BIT_WRITE;
static uint8_t ccc_val[2], info_val[1] = {0}, kex_val[32], scan_val[1] = {0}, join_val[1];

#define CHR(uuid, prop) {{ESP_GATT_AUTO_RSP}, {ESP_UUID_LEN_16, (uint8_t *)&CHAR_DECL, ESP_GATT_PERM_READ, 1, 1, (uint8_t *)&prop}}
#define VAL(uuid, perm, max, len, v) {{ESP_GATT_AUTO_RSP}, {ESP_UUID_LEN_128, (uint8_t *)uuid, perm, max, len, v}}
static const esp_gatts_attr_db_t db[A_N] = {
    [A_SVC] = {{ESP_GATT_AUTO_RSP}, {ESP_UUID_LEN_16, (uint8_t *)&PRIMARY, ESP_GATT_PERM_READ, 16, 16, (uint8_t *)U_SVC}},
    [A_INFO_C] = CHR(U_INFO, P_READ_NOTIFY),
    [A_INFO_V] = VAL(U_INFO, ESP_GATT_PERM_READ, 256, 1, info_val),
    [A_INFO_CCC] = {{ESP_GATT_AUTO_RSP},
                    {ESP_UUID_LEN_16, (uint8_t *)&CCC, ESP_GATT_PERM_READ | ESP_GATT_PERM_WRITE, 2, 2, ccc_val}},
    [A_KEX_C] = CHR(U_KEX, P_READ_WRITE),
    [A_KEX_V] = VAL(U_KEX, ESP_GATT_PERM_READ | ESP_GATT_PERM_WRITE, 32, 32, kex_val),
    [A_SCAN_C] = CHR(U_SCAN, P_READ_WRITE),
    [A_SCAN_V] = VAL(U_SCAN, ESP_GATT_PERM_READ | ESP_GATT_PERM_WRITE, 512, 1, scan_val),
    [A_JOIN_C] = CHR(U_JOIN, P_WRITE),
    [A_JOIN_V] = VAL(U_JOIN, ESP_GATT_PERM_WRITE, 200, 0, join_val),
};

static esp_ble_adv_params_t adv = {
    .adv_int_min = 0x40, .adv_int_max = 0x80, .adv_type = ADV_TYPE_IND,
    .own_addr_type = BLE_ADDR_TYPE_PUBLIC, .channel_map = ADV_CHNL_ALL, .adv_filter_policy = ADV_FILTER_ALLOW_SCAN_ANY_CON_ANY,
};

static void set_info(void) {
    static const char *names[] = {"setup", "code", "joining", "online", "failed"};
    char j[256];
    int n = snprintf(j, sizeof j, "{\"id\":\"%s\",\"fw\":\"%s\",\"state\":\"%s\",\"ssid\":\"%s\",\"ip\":\"%s\",\"err\":\"%s\"}",
                     bridge.id, BRIDGE_FW, names[bridge.state], bridge.state >= ST_JOINING ? settings.ssid : "",
                     bridge.ip, bridge.err);
    if (handle[A_INFO_V]) esp_ble_gatts_set_attr_value(handle[A_INFO_V], n, (uint8_t *)j);
}

void setup_ble_notify_info(void) {
    set_info();
    if (conn == 0xFFFF || gif == ESP_GATT_IF_NONE) return;
    uint16_t len;
    const uint8_t *v;
    if (esp_ble_gatts_get_attr_value(handle[A_INFO_V], &len, &v) == ESP_OK)
        esp_ble_gatts_send_indicate(gif, conn, handle[A_INFO_V], len, (uint8_t *)v, false);
}

static void new_keys(void) {
    keyed = false;
    crypto_keypair(our_pub);
    if (handle[A_KEX_V]) esp_ble_gatts_set_attr_value(handle[A_KEX_V], 32, our_pub);
}

void setup_ble_open(bool open) {
    bridge.setup_open = open;
    if (open) {
        new_keys();
        if (bridge.state != ST_JOINING && bridge.state != ST_ONLINE) bridge.state = ST_SETUP;
        esp_ble_gap_start_advertising(&adv);
    } else {
        esp_ble_gap_stop_advertising();
        if (conn != 0xFFFF) esp_ble_gatts_close(gif, conn);
    }
    set_info();
    screen_update();
}

static void scan_task(void *arg) {
    static char out[512];
    int n = wifi_scan_json(out, sizeof out);
    esp_ble_gatts_set_attr_value(handle[A_SCAN_V], n, (uint8_t *)out);
    vTaskDelete(NULL);
}

static void on_write(esp_ble_gatts_cb_param_t *p) {
    uint16_t h = p->write.handle;
    if (h == handle[A_KEX_V] && p->write.len == 32) {
        uint8_t peer[32];
        memcpy(peer, p->write.value, 32);
        if (crypto_agree(peer, our_pub, &bridge.code, wifi_key, ctrl_key) == 0) {
            keyed = true;
            bridge.state = ST_CODE;
            ESP_LOGI(TAG, "keys agreed; code %06lu", (unsigned long)bridge.code);
        }
        esp_ble_gatts_set_attr_value(handle[A_KEX_V], 32, our_pub);  // keep ours readable
        set_info();
        screen_update();
    } else if (h == handle[A_SCAN_V]) {
        static const char s[] = "\"scanning\"";
        esp_ble_gatts_set_attr_value(handle[A_SCAN_V], sizeof s - 1, (const uint8_t *)s);
        xTaskCreate(scan_task, "scan", 4096, NULL, 5, NULL);
    } else if (h == handle[A_JOIN_V] && keyed) {
        uint8_t plain[100];
        int n = crypto_open(wifi_key, p->write.value, p->write.len, plain, sizeof plain - 1);
        if (n <= 0) {
            snprintf(bridge.err, sizeof bridge.err, "bad setup message");
            setup_ble_notify_info();
            return;
        }
        plain[n] = 0;
        const char *ssid = (char *)plain, *pass = (char *)plain + strlen(ssid) + 1;
        if (pass > (char *)plain + n) pass = "";
        snprintf(settings.ssid, sizeof settings.ssid, "%s", ssid);
        snprintf(settings.pass, sizeof settings.pass, "%s", pass);
        memcpy(settings.key, ctrl_key, 32);  // saved once the network works (wifi.c)
        wifi_join(settings.ssid, settings.pass);
    }
}

static void gatts_cb(esp_gatts_cb_event_t ev, esp_gatt_if_t g, esp_ble_gatts_cb_param_t *p) {
    switch (ev) {
    case ESP_GATTS_REG_EVT:
        gif = g;
        esp_ble_gatts_create_attr_tab(db, g, A_N, 0);
        break;
    case ESP_GATTS_CREAT_ATTR_TAB_EVT:
        if (p->add_attr_tab.status == ESP_GATT_OK) {
            memcpy(handle, p->add_attr_tab.handles, sizeof handle);
            esp_ble_gatts_start_service(handle[A_SVC]);
            set_info();
            esp_ble_gatts_set_attr_value(handle[A_KEX_V], 32, our_pub);
        }
        break;
    case ESP_GATTS_CONNECT_EVT:
        conn = p->connect.conn_id;
        new_keys();  // every connection starts a fresh exchange
        break;
    case ESP_GATTS_DISCONNECT_EVT:
        conn = 0xFFFF;
        if (bridge.state == ST_CODE) bridge.state = ST_SETUP;
        screen_update();
        if (bridge.setup_open) esp_ble_gap_start_advertising(&adv);
        break;
    case ESP_GATTS_WRITE_EVT:
        if (!p->write.is_prep) on_write(p);
        break;
    default:
        break;
    }
}

static void gap_cb(esp_gap_ble_cb_event_t ev, esp_ble_gap_cb_param_t *p) {}

void setup_ble_init(void) {
    esp_ble_gap_register_callback(gap_cb);
    esp_ble_gatts_register_callback(gatts_cb);
    esp_ble_gatts_app_register(0x42);
    esp_ble_gatt_set_local_mtu(247);
    char name[24];
    snprintf(name, sizeof name, "PhoneUse Bridge %.4s", bridge.id + 8);
    esp_ble_adv_data_t ad = {.set_scan_rsp = false, .include_name = true, .flag = ESP_BLE_ADV_FLAG_GEN_DISC | ESP_BLE_ADV_FLAG_BREDR_NOT_SPT};
    esp_ble_gap_set_device_name(name);
    esp_ble_gap_config_adv_data(&ad);
    esp_ble_adv_data_t rsp = {.set_scan_rsp = true, .service_uuid_len = 16, .p_service_uuid = (uint8_t *)U_SVC};
    esp_ble_gap_config_adv_data(&rsp);
    new_keys();
}
