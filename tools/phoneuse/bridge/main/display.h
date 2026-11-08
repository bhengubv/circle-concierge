// The T-Display's 1.14" ST7789 screen, landscape (240 x 135), drawn a text line
// at a time: no framebuffer, so the radios keep the RAM.
#pragma once
#include <stdint.h>

typedef struct {
    uint8_t w, h;           // glyph cell in pixels
    uint16_t per;           // bytes per glyph (4-bit pixels, two per byte)
    const uint8_t *bits;    // glyphs for ' '..'~'
} font_t;

#define RGB(r, g, b) ((uint16_t)((((r) & 0xF8) << 8) | (((g) & 0xFC) << 3) | ((b) >> 3)))
#define DISPLAY_W 240
#define DISPLAY_H 135

void display_init(void);
void display_clear(uint16_t color);
// One line of text in a band [y, y + font height), left-aligned at x (or centred
// when x < 0), the rest of the band filled with bg. Returns the band's height.
int display_text(int x, int y, const font_t *f, const char *s, uint16_t fg, uint16_t bg);
void display_backlight(int on);
