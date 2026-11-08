#include "display.h"

#include <string.h>

#include "driver/gpio.h"
#include "driver/spi_master.h"
#include "esp_heap_caps.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_st7789.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"

// LilyGO T-Display v1.1 wiring.
#define PIN_MOSI 19
#define PIN_SCLK 18
#define PIN_CS 5
#define PIN_DC 16
#define PIN_RST 23
#define PIN_BL 4

static esp_lcd_panel_handle_t panel;
static uint16_t *line;  // one text band, DISPLAY_W x tallest font
static SemaphoreHandle_t sent;  // the SPI transfer of `line` finished: it may be reused

static bool on_sent(esp_lcd_panel_io_handle_t io, esp_lcd_panel_io_event_data_t *e, void *ctx) {
    BaseType_t woke = pdFALSE;
    xSemaphoreGiveFromISR(sent, &woke);
    return woke == pdTRUE;
}

// draw_bitmap only queues the transfer (DMA): wait for it before the buffer is
// refilled, or the next line overwrites the bottom of this one mid-send.
static void push(int y, int h) {
    esp_lcd_panel_draw_bitmap(panel, 0, y, DISPLAY_W, y + h, line);
    xSemaphoreTake(sent, portMAX_DELAY);
}

void display_backlight(int on) { gpio_set_level(PIN_BL, on); }

void display_init(void) {
    gpio_config_t bl = {.pin_bit_mask = 1ULL << PIN_BL, .mode = GPIO_MODE_OUTPUT};
    gpio_config(&bl);
    spi_bus_config_t bus = {.mosi_io_num = PIN_MOSI, .sclk_io_num = PIN_SCLK, .miso_io_num = -1,
                            .quadwp_io_num = -1, .quadhd_io_num = -1,
                            .max_transfer_sz = DISPLAY_W * 48 * 2};
    ESP_ERROR_CHECK(spi_bus_initialize(SPI2_HOST, &bus, SPI_DMA_CH_AUTO));
    esp_lcd_panel_io_handle_t io;
    esp_lcd_panel_io_spi_config_t ioc = {.dc_gpio_num = PIN_DC, .cs_gpio_num = PIN_CS,
                                         .pclk_hz = 40 * 1000 * 1000, .lcd_cmd_bits = 8,
                                         .lcd_param_bits = 8, .spi_mode = 0, .trans_queue_depth = 4,
                                         .on_color_trans_done = on_sent};
    sent = xSemaphoreCreateBinary();
    ESP_ERROR_CHECK(esp_lcd_new_panel_io_spi((esp_lcd_spi_bus_handle_t)SPI2_HOST, &ioc, &io));
    esp_lcd_panel_dev_config_t pc = {.reset_gpio_num = PIN_RST, .rgb_ele_order = LCD_RGB_ELEMENT_ORDER_RGB,
                                     .bits_per_pixel = 16};
    ESP_ERROR_CHECK(esp_lcd_new_panel_st7789(io, &pc, &panel));
    esp_lcd_panel_reset(panel);
    esp_lcd_panel_init(panel);
    esp_lcd_panel_invert_color(panel, true);  // IPS panel
    esp_lcd_panel_swap_xy(panel, true);       // landscape, USB port on the right
    esp_lcd_panel_mirror(panel, true, false);
    esp_lcd_panel_set_gap(panel, 40, 53);
    esp_lcd_panel_disp_on_off(panel, true);
    line = heap_caps_malloc(DISPLAY_W * 48 * 2, MALLOC_CAP_DMA);
    display_clear(0);
    display_backlight(1);
}

static void band(int y, int h, uint16_t bg) {
    uint16_t c = (bg >> 8) | (bg << 8);  // the panel wants big-endian pixels
    for (int i = 0; i < DISPLAY_W * h; i++) line[i] = c;
}

void display_clear(uint16_t color) {
    for (int y = 0; y < DISPLAY_H; y += 45) {
        int h = DISPLAY_H - y < 45 ? DISPLAY_H - y : 45;
        band(y, h, color);
        push(y, h);
    }
}

static uint16_t mix(uint16_t fg, uint16_t bg, int a) {  // a: 0..15
    int r = ((fg >> 11) * a + (bg >> 11) * (15 - a)) / 15;
    int g = (((fg >> 5) & 63) * a + ((bg >> 5) & 63) * (15 - a)) / 15;
    int b = ((fg & 31) * a + (bg & 31) * (15 - a)) / 15;
    uint16_t c = (r << 11) | (g << 5) | b;
    return (c >> 8) | (c << 8);
}

int display_text(int x, int y, const font_t *f, const char *s, uint16_t fg, uint16_t bg) {
    int n = strlen(s), h = f->h;
    if (h > 48) h = 48;
    if (x < 0) x = (DISPLAY_W - n * f->w) / 2;
    if (x < 0) x = 0;
    band(y, h, bg);
    for (int i = 0; i < n; i++) {
        int c = (unsigned char)s[i];
        if (c < 32 || c > 126) c = '?';
        const uint8_t *g = f->bits + (c - 32) * f->per;
        for (int py = 0; py < h; py++)
            for (int px = 0; px < f->w; px++) {
                int X = x + i * f->w + px;
                if (X >= DISPLAY_W) break;
                int k = py * f->w + px, a = (g[k >> 1] >> ((k & 1) ? 0 : 4)) & 15;
                if (a) line[py * DISPLAY_W + X] = mix(fg, bg, a);
            }
    }
    if (y + h > DISPLAY_H) h = DISPLAY_H - y;
    if (h > 0) push(y, h);
    return f->h;
}
