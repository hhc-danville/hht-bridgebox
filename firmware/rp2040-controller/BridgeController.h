// HHT Bridge Box controller - protocol and safety logic (hardware independent).
// Hammer Hill Technologies. Used by HHT_BridgeController.ino (RP2040) and by the host unit test.
//
// Protocol: ASCII lines ending in '\n' (USB CDC serial, 115200 8N1). Case-insensitive.
//   HHT?            -> HHT-BRIDGEBOX-CTRL 1.1 PWR RST USBSW LED SENS
//   PWR SHORT       -> OK PWR SHORT   (presses the target's power button 400 ms)
//   PWR LONG        -> OK PWR LONG    (holds it 6 s = forced power off)
//   RST             -> OK RST         (presses reset 300 ms)
//   USB HOST|TARGET -> OK USB HOST|TARGET (switches the shared USB port, with a 250 ms disconnect)
//   STAT?           -> STAT PWRLED=0|1 HDDLED=0|1 USB=HOST|TARGET BUSY=0|1
//   SENS?           -> SENS T1=41.5 T2=NA FAN1=1320   (sensor clip: NTC probes in C, fan tach in RPM)
//   anything else   -> ERR <reason>
#pragma once
#include <stdint.h>
#include <stddef.h>

namespace hht {

// Everything the logic needs from the board. The RP2040 sketch implements it with GPIO.
class Hal {
public:
    virtual ~Hal() {}
    virtual void setPowerButton(bool pressed) = 0;   // drives the PWR opto-isolator
    virtual void setResetButton(bool pressed) = 0;   // drives the RST opto-isolator
    virtual void setUsbEnabled(bool enabled) = 0;    // USB mux output enable
    virtual void setUsbToTarget(bool toTarget) = 0;  // USB mux select
    virtual bool powerLedOn() = 0;                   // target power LED (via opto)
    virtual bool hddLedOn() = 0;                     // target HDD LED (via opto)
    virtual uint32_t millis() = 0;
    // Sensor clip (optional; defaults = not fitted)
    virtual bool readThermistorRaw(int probe, uint16_t& raw12) { (void)probe; (void)raw12; return false; }  // 12-bit ADC
    virtual uint32_t fanPulseCount() { return 0; }   // running count of tach pulses (2 per revolution)
    virtual void write(const char* text) = 0;        // sends text to the host
};

class BridgeController {
public:
    static const uint32_t SHORT_PRESS_MS = 400;
    static const uint32_t LONG_PRESS_MS = 6000;
    static const uint32_t RESET_PRESS_MS = 300;
    static const uint32_t USB_GAP_MS = 250;
    static const uint32_t HDD_HOLD_MS = 250;      // HDD LED flickers; report activity seen recently
    static const size_t MAX_LINE = 63;
    static const char* VERSION;

    explicit BridgeController(Hal& hal) : hal_(hal) {}

    void begin();
    void onChar(char c);
    void tick();                // call often (every loop)
    bool busy() const { return pressUntil_ != 0 || usbReconnectAt_ != 0; }
    uint32_t fanRpm() const { return fanRpm_; }

    // 10k NTC (B=3950) to GND with a 10k series resistor to 3.3 V, read on a 12-bit ADC. Returns false if open/short.
    static bool thermistorToCelsius(uint16_t raw12, float& celsius);
    bool usbToTarget() const { return usbTarget_; }

private:
    enum Button { NONE, POWER, RESET };

    void handleLine(const char* line);
    void startPress(Button b, uint32_t ms);
    void reply(const char* text);
    static bool equalsIgnoreCase(const char* a, const char* b);

    Hal& hal_;
    char line_[MAX_LINE + 1] = {0};
    size_t len_ = 0;
    bool overflow_ = false;
    Button pressed_ = NONE;
    uint32_t pressUntil_ = 0;
    uint32_t usbReconnectAt_ = 0;
    bool usbTarget_ = false;
    uint32_t lastHddActive_ = 0;
    uint32_t fanWindowStart_ = 0;
    uint32_t fanPulsesAtStart_ = 0;
    uint32_t fanRpm_ = 0;
    bool hddSeen_ = false;
};

} // namespace hht
