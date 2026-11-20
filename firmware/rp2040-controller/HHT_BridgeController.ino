// HHT Bridge Box controller firmware - Raspberry Pi Pico / any RP2040 board.
// Hammer Hill Technologies.
//
// Build: Arduino IDE 2 + "Raspberry Pi Pico/RP2040" core by Earle Philhower
//   Board: Raspberry Pi Pico, USB Stack: Pico SDK, then Upload (hold BOOTSEL on first flash).
// The logic lives in BridgeController.cpp (unit-tested on a PC: see test/).
//
// Wiring (see docs/HARDWARE_V1.md for the full schematic notes):
//   GP2  -> PWR opto-isolator LED (PC817 via 330R). Opto transistor across target PWR_SW header pins.
//   GP3  -> RST opto-isolator LED (PC817 via 330R). Opto transistor across target RESET_SW header pins.
//   GP6  -> USB mux SEL (TS3USB221: LOW = laptop/HOST side, HIGH = TARGET side)
//   GP7  -> USB mux /OE (active LOW; HIGH disconnects both sides)
//   GP10 <- PWR LED sense (opto output, internal pull-up, LOW = LED on)
//   GP11 <- HDD LED sense (opto output, internal pull-up, LOW = LED on)
//   GP26 <- sensor clip probe 1: 10k NTC (B3950) to GND, 10k resistor to 3V3 (ADC0)
//   GP27 <- sensor clip probe 2: same (ADC1)
//   GP12 <- fan tach (3/4-pin fan yellow wire) via 10k pull-up to 3V3 + 1k series; fan GND shared with target PSU GND ONLY
//           through the sensor clip's own isolated adapter (see docs/HARDWARE_V1.md) - never tie to the laptop side directly
//   LED_BUILTIN: status (blinks while a button is held / USB is switching)
//
// SAFETY: the motherboard front-panel header must ONLY be driven through the opto-isolators.
// Never connect a GPIO pin directly to the target computer.

#include <Arduino.h>
#include "hardware/watchdog.h"
#include "BridgeController.h"

static const uint8_t PIN_PWR = 2;
static const uint8_t PIN_RST = 3;
static const uint8_t PIN_USB_SEL = 6;
static const uint8_t PIN_USB_OE_N = 7;
static const uint8_t PIN_PWR_LED = 10;
static const uint8_t PIN_HDD_LED = 11;
static const uint8_t PIN_FAN_TACH = 12;
static const uint8_t PIN_NTC1 = 26;
static const uint8_t PIN_NTC2 = 27;

static volatile uint32_t fanPulses = 0;
static void onFanPulse() { fanPulses++; }

class PicoHal : public hht::Hal {
public:
    void setPowerButton(bool pressed) override { digitalWrite(PIN_PWR, pressed ? HIGH : LOW); }
    void setResetButton(bool pressed) override { digitalWrite(PIN_RST, pressed ? HIGH : LOW); }
    void setUsbEnabled(bool enabled) override { digitalWrite(PIN_USB_OE_N, enabled ? LOW : HIGH); }
    void setUsbToTarget(bool toTarget) override { digitalWrite(PIN_USB_SEL, toTarget ? HIGH : LOW); }
    bool powerLedOn() override { return digitalRead(PIN_PWR_LED) == LOW; }
    bool hddLedOn() override { return digitalRead(PIN_HDD_LED) == LOW; }
    uint32_t millis() override { return ::millis(); }
    bool readThermistorRaw(int probe, uint16_t& raw12) override {
        raw12 = (uint16_t)analogRead(probe == 0 ? PIN_NTC1 : PIN_NTC2);
        return true;   // open/short probes are rejected by thermistorToCelsius
    }
    uint32_t fanPulseCount() override { return fanPulses; }
    void write(const char* text) override { Serial.print(text); }
};

static PicoHal hal;
static hht::BridgeController controller(hal);

void setup() {
    // Outputs low (released) before anything else so a reboot can never "press" a button.
    pinMode(PIN_PWR, OUTPUT);      digitalWrite(PIN_PWR, LOW);
    pinMode(PIN_RST, OUTPUT);      digitalWrite(PIN_RST, LOW);
    pinMode(PIN_USB_SEL, OUTPUT);  digitalWrite(PIN_USB_SEL, LOW);
    pinMode(PIN_USB_OE_N, OUTPUT); digitalWrite(PIN_USB_OE_N, LOW);
    pinMode(PIN_PWR_LED, INPUT_PULLUP);
    pinMode(PIN_HDD_LED, INPUT_PULLUP);
    pinMode(LED_BUILTIN, OUTPUT);
    analogReadResolution(12);
    pinMode(PIN_FAN_TACH, INPUT_PULLUP);
    attachInterrupt(digitalPinToInterrupt(PIN_FAN_TACH), onFanPulse, FALLING);

    Serial.begin(115200);
    controller.begin();
    watchdog_enable(2000, true);   // a hung loop resets the board, which releases every output
}

void loop() {
    while (Serial.available() > 0) controller.onChar((char)Serial.read());
    controller.tick();
    digitalWrite(LED_BUILTIN, controller.busy() ? ((millis() / 100) % 2) : HIGH);
    watchdog_update();
}
