#include "settings.h"

#include <string.h>

#include "nvs.h"
#include "nvs_flash.h"

bridge_config_t settings;

void settings_load(void) {
    esp_err_t e = nvs_flash_init();
    if (e == ESP_ERR_NVS_NO_FREE_PAGES || e == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        nvs_flash_erase();
        nvs_flash_init();
    }
    memset(&settings, 0, sizeof settings);
    nvs_handle_t h;
    if (nvs_open("bridge", NVS_READONLY, &h) != ESP_OK) return;
    size_t n = sizeof settings.ssid;
    bool ok = nvs_get_str(h, "ssid", settings.ssid, &n) == ESP_OK;
    n = sizeof settings.pass;
    ok = ok && nvs_get_str(h, "pass", settings.pass, &n) == ESP_OK;
    n = sizeof settings.key;
    ok = ok && nvs_get_blob(h, "key", settings.key, &n) == ESP_OK && n == sizeof settings.key;
    settings.configured = ok;
    nvs_close(h);
}

void settings_save(void) {
    nvs_handle_t h;
    if (nvs_open("bridge", NVS_READWRITE, &h) != ESP_OK) return;
    nvs_set_str(h, "ssid", settings.ssid);
    nvs_set_str(h, "pass", settings.pass);
    nvs_set_blob(h, "key", settings.key, sizeof settings.key);
    nvs_commit(h);
    nvs_close(h);
    settings.configured = true;
}

void settings_erase(void) {
    nvs_handle_t h;
    if (nvs_open("bridge", NVS_READWRITE, &h) == ESP_OK) {
        nvs_erase_all(h);
        nvs_commit(h);
        nvs_close(h);
    }
    memset(&settings, 0, sizeof settings);
}
