idf_component_register(
    SRCS "main.c" "display.c" "settings.c" "crypto.c" "setup_ble.c" "wifi.c" "hid.c" "link.c" "screen.c"
    INCLUDE_DIRS "."
    REQUIRES bt esp_coex esp_wifi esp_netif esp_event nvs_flash esp_lcd esp_driver_gpio esp_driver_spi esp_adc mbedtls esp_timer
)
target_compile_options(${COMPONENT_LIB} PRIVATE -Wno-format-truncation -Wno-stringop-truncation)
