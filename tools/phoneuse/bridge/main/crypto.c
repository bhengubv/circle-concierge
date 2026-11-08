#include "crypto.h"

#include <string.h>

#include "esp_random.h"
#include "psa/crypto.h"

static psa_key_id_t priv;

void crypto_init(void) { psa_crypto_init(); }

void crypto_random(void *buf, size_t n) { esp_fill_random(buf, n); }

int crypto_keypair(uint8_t pub[32]) {
    if (priv) psa_destroy_key(priv);
    priv = 0;
    psa_key_attributes_t a = PSA_KEY_ATTRIBUTES_INIT;
    psa_set_key_type(&a, PSA_KEY_TYPE_ECC_KEY_PAIR(PSA_ECC_FAMILY_MONTGOMERY));
    psa_set_key_bits(&a, 255);
    psa_set_key_usage_flags(&a, PSA_KEY_USAGE_DERIVE);
    psa_set_key_algorithm(&a, PSA_ALG_ECDH);
    if (psa_generate_key(&a, &priv) != PSA_SUCCESS) return -1;
    size_t n;
    return psa_export_public_key(priv, pub, 32, &n) == PSA_SUCCESS && n == 32 ? 0 : -1;
}

static int hkdf(const uint8_t *secret, size_t sn, const uint8_t *salt, size_t saltn, const char *info,
                uint8_t *out, size_t n) {
    psa_key_derivation_operation_t op = PSA_KEY_DERIVATION_OPERATION_INIT;
    int ok = psa_key_derivation_setup(&op, PSA_ALG_HKDF(PSA_ALG_SHA_256)) == PSA_SUCCESS &&
             psa_key_derivation_input_bytes(&op, PSA_KEY_DERIVATION_INPUT_SALT, salt, saltn) == PSA_SUCCESS &&
             psa_key_derivation_input_bytes(&op, PSA_KEY_DERIVATION_INPUT_SECRET, secret, sn) == PSA_SUCCESS &&
             psa_key_derivation_input_bytes(&op, PSA_KEY_DERIVATION_INPUT_INFO, (const uint8_t *)info,
                                            strlen(info)) == PSA_SUCCESS &&
             psa_key_derivation_output_bytes(&op, out, n) == PSA_SUCCESS;
    psa_key_derivation_abort(&op);
    return ok ? 0 : -1;
}

int crypto_agree(const uint8_t peer[32], const uint8_t ours[32], uint32_t *code, uint8_t wifi_key[32],
                 uint8_t ctrl_key[32]) {
    uint8_t shared[32], salt[64], c[4];
    size_t n;
    if (psa_raw_key_agreement(PSA_ALG_ECDH, priv, peer, 32, shared, sizeof shared, &n) != PSA_SUCCESS) return -1;
    memcpy(salt, ours, 32);
    memcpy(salt + 32, peer, 32);
    if (hkdf(shared, n, salt, 64, "phoneuse code", c, 4) || hkdf(shared, n, salt, 64, "phoneuse wifi", wifi_key, 32) ||
        hkdf(shared, n, salt, 64, "phoneuse control", ctrl_key, 32))
        return -1;
    *code = ((uint32_t)c[0] << 24 | (uint32_t)c[1] << 16 | (uint32_t)c[2] << 8 | c[3]) % 1000000;
    memset(shared, 0, sizeof shared);
    return 0;
}

static psa_key_id_t import(const uint8_t key[32], psa_key_type_t type, psa_algorithm_t alg, psa_key_usage_t use) {
    psa_key_attributes_t a = PSA_KEY_ATTRIBUTES_INIT;
    psa_set_key_type(&a, type);
    psa_set_key_bits(&a, 256);
    psa_set_key_usage_flags(&a, use);
    psa_set_key_algorithm(&a, alg);
    psa_key_id_t id = 0;
    psa_import_key(&a, key, 32, &id);
    return id;
}

int crypto_open(const uint8_t key[32], const uint8_t *in, size_t n, uint8_t *out, size_t cap) {
    if (n < 12 + 16) return -1;
    psa_key_id_t k = import(key, PSA_KEY_TYPE_AES, PSA_ALG_GCM, PSA_KEY_USAGE_DECRYPT);
    size_t len = 0;
    psa_status_t s = psa_aead_decrypt(k, PSA_ALG_GCM, in, 12, NULL, 0, in + 12, n - 12, out, cap, &len);
    psa_destroy_key(k);
    return s == PSA_SUCCESS ? (int)len : -1;
}

void crypto_hmac(const uint8_t key[32], const uint8_t *data, size_t n, uint8_t mac[32]) {
    // Called for every control packet: keep the imported key between calls.
    static uint8_t last[32];
    static psa_key_id_t k;
    if (!k || memcmp(last, key, 32)) {
        if (k) psa_destroy_key(k);
        k = import(key, PSA_KEY_TYPE_HMAC, PSA_ALG_HMAC(PSA_ALG_SHA_256), PSA_KEY_USAGE_SIGN_MESSAGE);
        memcpy(last, key, 32);
    }
    size_t len;
    psa_mac_compute(k, PSA_ALG_HMAC(PSA_ALG_SHA_256), data, n, mac, 32, &len);
}
