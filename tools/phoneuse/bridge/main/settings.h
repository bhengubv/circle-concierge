// What the bridge keeps across reboots (NVS): the Wi-Fi network it joins and the
// control key it shares with the phoneuse PC that set it up.
#pragma once
#include <stdbool.h>
#include <stdint.h>

typedef struct {
    char ssid[33];
    char pass[65];
    uint8_t key[32];  // signs every control packet (HMAC-SHA256)
    bool configured;
} bridge_config_t;

extern bridge_config_t settings;
void settings_load(void);
void settings_save(void);
void settings_erase(void);
