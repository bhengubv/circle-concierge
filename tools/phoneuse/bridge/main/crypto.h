// Setup key exchange and packet authentication.
//
// Setup: the PC and the bridge exchange X25519 public keys over BLE. Both derive,
// with HKDF-SHA256 over the shared secret (salt: bridge key || PC key):
//   "phoneuse code"    -> a 6-digit code, shown on the screen and on the PC; the
//                         person compares them (a different code means someone
//                         sits in between)
//   "phoneuse wifi"    -> AES-256-GCM key for the Wi-Fi password
//   "phoneuse control" -> HMAC-SHA256 key that signs every UDP control packet
#pragma once
#include <stddef.h>
#include <stdint.h>

void crypto_init(void);
// New X25519 key pair; writes our public key (32 bytes).
int crypto_keypair(uint8_t pub[32]);
// Agree with the peer's public key; derives the code and the keys.
int crypto_agree(const uint8_t peer[32], const uint8_t ours[32], uint32_t *code, uint8_t wifi_key[32],
                 uint8_t ctrl_key[32]);
// AES-256-GCM open: in = nonce(12) || ciphertext || tag(16). Returns plaintext length or -1.
int crypto_open(const uint8_t key[32], const uint8_t *in, size_t n, uint8_t *out, size_t cap);
void crypto_hmac(const uint8_t key[32], const uint8_t *data, size_t n, uint8_t mac[32]);
void crypto_random(void *buf, size_t n);
