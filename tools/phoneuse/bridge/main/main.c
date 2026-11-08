// phoneuse Bluetooth bridge for the LilyGO T-Display (ESP32): the iPhone pairs with
// it as a Bluetooth keyboard and mouse, and phoneuse drives it over Wi-Fi (UDP).
// Setup happens over BLE from phoneuse; see setup_ble.c and link.c.
#include <stdio.h>

#include "bridge.h"
#include "settings.h"
#include "crypto.h"
#include "display.h"
#include "driver/gpio.h"
#include "esp_adc/adc_cali.h"
#include "esp_adc/adc_cali_scheme.h"
#include "esp_adc/adc_oneshot.h"
#include "esp_bt.h"
#include "esp_bt_main.h"
#include "esp_log.h"
#include "esp_mac.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#define BUTTON_SETUP 35  // the button away from the USB port: hold 3 s to set up again
#define ADC_ENABLE 14    // powers the battery voltage divider (GPIO 34)

bridge_t bridge;
void screen_start(void);

static void bt_start(void) {
    esp_bt_controller_config_t cfg = BT_CONTROLLER_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_bt_controller_init(&cfg));
    ESP_ERROR_CHECK(esp_bt_controller_enable(ESP_BT_MODE_BTDM));
    esp_bluedroid_config_t bc = BT_BLUEDROID_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_bluedroid_init_with_cfg(&bc));
    ESP_ERROR_CHECK(esp_bluedroid_enable());
}

static void housekeeping(void *arg) {
    gpio_config_t in = {.pin_bit_mask = 1ULL << BUTTON_SETUP, .mode = GPIO_MODE_INPUT};
    gpio_config(&in);
    gpio_config_t en = {.pin_bit_mask = 1ULL << ADC_ENABLE, .mode = GPIO_MODE_OUTPUT};
    gpio_config(&en);
    gpio_set_level(ADC_ENABLE, 1);
    adc_oneshot_unit_handle_t adc;
    adc_oneshot_unit_init_cfg_t uc = {.unit_id = ADC_UNIT_1};
    adc_oneshot_new_unit(&uc, &adc);
    adc_oneshot_chan_cfg_t cc = {.atten = ADC_ATTEN_DB_12, .bitwidth = ADC_BITWIDTH_DEFAULT};
    adc_oneshot_config_channel(adc, ADC_CHANNEL_6, &cc);  // GPIO 34
    adc_cali_handle_t cal = NULL;
    adc_cali_line_fitting_config_t lc = {.unit_id = ADC_UNIT_1, .atten = ADC_ATTEN_DB_12,
                                         .bitwidth = ADC_BITWIDTH_DEFAULT};
    adc_cali_create_scheme_line_fitting(&lc, &cal);
    int held = 0, tick = 0;
    for (;;) {
        vTaskDelay(pdMS_TO_TICKS(100));
        held = gpio_get_level(BUTTON_SETUP) == 0 ? held + 1 : 0;
        if (held == 30) setup_ble_open(true);
        if (++tick % 10) continue;
        // Keep the phone connected: page it every 5 s while it's away (locked,
        // out of range, Bluetooth toggled). iOS accepts a bonded keyboard's page.
        if (!bridge.phone_connected && tick % 50 == 0) hid_connect_last();
        wifi_ap_record_t ap;
        bridge.wifi_rssi = esp_wifi_sta_get_ap_info(&ap) == ESP_OK ? ap.rssi : 0;
        int raw, mv;
        if (tick % 100 == 10 && adc_oneshot_read(adc, ADC_CHANNEL_6, &raw) == ESP_OK && cal &&
            adc_cali_raw_to_voltage(cal, raw, &mv) == ESP_OK)
            bridge.battery_mv = mv * 2;  // 100k/100k divider
    }
}

void app_main(void) {
    uint8_t mac[6];
    esp_read_mac(mac, ESP_MAC_WIFI_STA);
    snprintf(bridge.id, sizeof bridge.id, "%02x%02x%02x%02x%02x%02x", mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    settings_load();
    crypto_init();
    display_init();
    bridge.state = ST_SETUP;
    screen_start();
    bt_start();
    hid_init();
    setup_ble_init();
    wifi_init();
    link_start();
    if (!settings.configured) setup_ble_open(true);
    xTaskCreate(housekeeping, "house", 4096, NULL, 2, NULL);
    ESP_LOGI("main", "bridge %s up, free heap %lu", bridge.id, (unsigned long)esp_get_free_heap_size());
}
