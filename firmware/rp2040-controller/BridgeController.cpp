#include "BridgeController.h"
#include <ctype.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <stdio.h>

namespace hht {

const char* BridgeController::VERSION = "1.1";

static uint32_t nonZero(uint32_t t) { return t == 0 ? 1 : t; }  // 0 means "no timer"

void BridgeController::begin() {
    // Safe state: nothing pressed, shared USB port on the technician laptop.
    hal_.setPowerButton(false);
    hal_.setResetButton(false);
    usbTarget_ = false;
    hal_.setUsbToTarget(false);
    hal_.setUsbEnabled(true);
    pressed_ = NONE;
    pressUntil_ = 0;
    usbReconnectAt_ = 0;
    len_ = 0;
    overflow_ = false;
}

void BridgeController::onChar(char c) {
    if (c == '\r') return;
    if (c == '\n') {
        if (overflow_) {
            reply("ERR LINE_TOO_LONG");
        } else if (len_ > 0) {
            line_[len_] = 0;
            handleLine(line_);
        }
        len_ = 0;
        overflow_ = false;
        return;
    }
    if ((unsigned char)c < 0x20 || (unsigned char)c > 0x7E) return;  // ignore binary noise
    if (len_ >= MAX_LINE) { overflow_ = true; return; }
    line_[len_++] = c;
}

void BridgeController::tick() {
    uint32_t now = hal_.millis();
    if (pressUntil_ != 0 && (int32_t)(now - pressUntil_) >= 0) {
        if (pressed_ == POWER) hal_.setPowerButton(false);
        if (pressed_ == RESET) hal_.setResetButton(false);
        pressed_ = NONE;
        pressUntil_ = 0;
    }
    if (usbReconnectAt_ != 0 && (int32_t)(now - usbReconnectAt_) >= 0) {
        hal_.setUsbToTarget(usbTarget_);
        hal_.setUsbEnabled(true);
        usbReconnectAt_ = 0;
    }
    if (hal_.hddLedOn()) { lastHddActive_ = now; hddSeen_ = true; }
    // Fan RPM over 1 s windows: 2 tach pulses per revolution.
    if ((uint32_t)(now - fanWindowStart_) >= 1000) {
        uint32_t pulses = hal_.fanPulseCount();
        uint32_t elapsed = now - fanWindowStart_;
        fanRpm_ = (uint32_t)((uint64_t)(pulses - fanPulsesAtStart_) * 30000u / (elapsed ? elapsed : 1));
        fanPulsesAtStart_ = pulses;
        fanWindowStart_ = now;
    }
}

bool BridgeController::thermistorToCelsius(uint16_t raw12, float& celsius) {
    if (raw12 < 20 || raw12 > 4075) return false;            // probe missing (open) or shorted
    const float rSeries = 10000.0f, r0 = 10000.0f, beta = 3950.0f, t0 = 298.15f;
    float v = raw12 / 4095.0f;
    float rNtc = rSeries * v / (1.0f - v);
    float invT = 1.0f / t0 + logf(rNtc / r0) / beta;
    celsius = 1.0f / invT - 273.15f;
    return celsius > -40.0f && celsius < 150.0f;
}

void BridgeController::startPress(Button b, uint32_t ms) {
    pressed_ = b;
    if (b == POWER) hal_.setPowerButton(true);
    if (b == RESET) hal_.setResetButton(true);
    pressUntil_ = nonZero(hal_.millis() + ms);
}

void BridgeController::reply(const char* text) {
    hal_.write(text);
    hal_.write("\n");
}

bool BridgeController::equalsIgnoreCase(const char* a, const char* b) {
    while (*a && *b) {
        if (toupper((unsigned char)*a) != toupper((unsigned char)*b)) return false;
        ++a; ++b;
    }
    return *a == 0 && *b == 0;
}

void BridgeController::handleLine(const char* raw) {
    // trim
    while (*raw == ' ') ++raw;
    char line[MAX_LINE + 1];
    strncpy(line, raw, MAX_LINE);
    line[MAX_LINE] = 0;
    size_t n = strlen(line);
    while (n > 0 && line[n - 1] == ' ') line[--n] = 0;

    if (equalsIgnoreCase(line, "HHT?")) {
        char buf[80];
        strcpy(buf, "HHT-BRIDGEBOX-CTRL ");
        strcat(buf, VERSION);
        strcat(buf, " PWR RST USBSW LED SENS");
        reply(buf);
        return;
    }
    if (equalsIgnoreCase(line, "STAT?")) {
        uint32_t now = hal_.millis();
        bool hdd = hal_.hddLedOn() || (hddSeen_ && (uint32_t)(now - lastHddActive_) < HDD_HOLD_MS);
        char buf[80];
        strcpy(buf, "STAT PWRLED=");
        strcat(buf, hal_.powerLedOn() ? "1" : "0");
        strcat(buf, " HDDLED=");
        strcat(buf, hdd ? "1" : "0");
        strcat(buf, " USB=");
        strcat(buf, usbTarget_ ? "TARGET" : "HOST");
        strcat(buf, " BUSY=");
        strcat(buf, busy() ? "1" : "0");
        reply(buf);
        return;
    }

    if (equalsIgnoreCase(line, "SENS?")) {
        char buf[96];
        strcpy(buf, "SENS");
        for (int p = 0; p < 2; ++p) {
            char part[24];
            uint16_t raw = 0;
            float c = 0;
            if (hal_.readThermistorRaw(p, raw) && thermistorToCelsius(raw, c)) {
                int tenths = (int)lroundf(c * 10.0f);
                snprintf(part, sizeof part, " T%d=%s%d.%d", p + 1, tenths < 0 ? "-" : "", abs(tenths) / 10, abs(tenths) % 10);
            } else {
                snprintf(part, sizeof part, " T%d=NA", p + 1);
            }
            strcat(buf, part);
        }
        char fan[24];
        snprintf(fan, sizeof fan, " FAN1=%lu", (unsigned long)fanRpm_);
        strcat(buf, fan);
        reply(buf);
        return;
    }

    bool isPwrShort = equalsIgnoreCase(line, "PWR SHORT");
    bool isPwrLong = equalsIgnoreCase(line, "PWR LONG");
    bool isRst = equalsIgnoreCase(line, "RST");
    bool isUsbHost = equalsIgnoreCase(line, "USB HOST");
    bool isUsbTarget = equalsIgnoreCase(line, "USB TARGET");

    if (isPwrShort || isPwrLong || isRst) {
        if (pressUntil_ != 0) { reply("ERR BUSY"); return; }  // never overlap button presses
        if (isRst) { startPress(RESET, RESET_PRESS_MS); reply("OK RST"); return; }
        startPress(POWER, isPwrLong ? LONG_PRESS_MS : SHORT_PRESS_MS);
        reply(isPwrLong ? "OK PWR LONG" : "OK PWR SHORT");
        return;
    }
    if (isUsbHost || isUsbTarget) {
        if (usbReconnectAt_ != 0) { reply("ERR BUSY"); return; }
        usbTarget_ = isUsbTarget;
        // Disconnect both sides first so each computer sees a clean unplug/replug.
        hal_.setUsbEnabled(false);
        usbReconnectAt_ = nonZero(hal_.millis() + USB_GAP_MS);
        reply(isUsbTarget ? "OK USB TARGET" : "OK USB HOST");
        return;
    }
    reply("ERR UNKNOWN");
}

} // namespace hht
