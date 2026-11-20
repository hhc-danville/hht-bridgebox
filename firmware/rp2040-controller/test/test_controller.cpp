// Host unit test for the controller logic:  g++ -std=c++17 -I.. ../BridgeController.cpp test_controller.cpp && ./a.out
#include "../BridgeController.h"
#include <cstdio>
#include <string>
#include <vector>

using namespace hht;

struct FakeHal : Hal {
    bool pwr = false, rst = false, usbEn = false, usbTarget = false, pwrLed = false, hddLed = false;
    uint32_t now = 1000;
    int rawT1 = -1;            // -1 = probe not fitted
    uint32_t pulses = 0;
    bool readThermistorRaw(int probe, uint16_t& raw) override { if (probe != 0 || rawT1 < 0) return false; raw = (uint16_t)rawT1; return true; }
    uint32_t fanPulseCount() override { return pulses; }
    std::string out;
    std::vector<std::string> log;
    void setPowerButton(bool p) override { pwr = p; log.push_back(std::string("PWR=") + (p ? "1" : "0")); }
    void setResetButton(bool p) override { rst = p; log.push_back(std::string("RST=") + (p ? "1" : "0")); }
    void setUsbEnabled(bool e) override { usbEn = e; }
    void setUsbToTarget(bool t) override { usbTarget = t; }
    bool powerLedOn() override { return pwrLed; }
    bool hddLedOn() override { return hddLed; }
    uint32_t millis() override { return now; }
    void write(const char* t) override { out += t; }
};

static int failures = 0;
#define CHECK(cond) do { if (!(cond)) { std::printf("FAIL line %d: %s\n", __LINE__, #cond); failures++; } } while (0)

static std::string send(BridgeController& c, FakeHal& h, const char* s) {
    h.out.clear();
    for (const char* p = s; *p; ++p) c.onChar(*p);
    return h.out;
}
static void advance(BridgeController& c, FakeHal& h, uint32_t ms) {
    for (uint32_t i = 0; i < ms; i += 10) { h.now += 10; c.tick(); }
}

int main() {
    FakeHal h;
    BridgeController c(h);
    c.begin();
    CHECK(!h.pwr && !h.rst && h.usbEn && !h.usbTarget);

    CHECK(send(c, h, "HHT?\n") == "HHT-BRIDGEBOX-CTRL 1.1 PWR RST USBSW LED SENS\n");
    CHECK(send(c, h, "hht?\r\n") == "HHT-BRIDGEBOX-CTRL 1.1 PWR RST USBSW LED SENS\n");   // case + CRLF
    CHECK(send(c, h, "STAT?\n") == "STAT PWRLED=0 HDDLED=0 USB=HOST BUSY=0\n");

    // short power press: down now, up after 400 ms
    CHECK(send(c, h, "PWR SHORT\n") == "OK PWR SHORT\n");
    CHECK(h.pwr && c.busy());
    CHECK(send(c, h, "RST\n") == "ERR BUSY\n");        // no overlapping presses
    CHECK(!h.rst);
    advance(c, h, 390); CHECK(h.pwr);
    advance(c, h, 20);  CHECK(!h.pwr && !c.busy());

    // long press holds 6 s
    send(c, h, "pwr long\n");
    advance(c, h, 5900); CHECK(h.pwr);
    advance(c, h, 200);  CHECK(!h.pwr);

    // reset
    CHECK(send(c, h, "RST\n") == "OK RST\n");
    CHECK(h.rst); advance(c, h, 310); CHECK(!h.rst);

    // USB switch: disconnect, then reconnect to the new side after 250 ms
    CHECK(send(c, h, "USB TARGET\n") == "OK USB TARGET\n");
    CHECK(!h.usbEn);
    CHECK(send(c, h, "USB HOST\n") == "ERR BUSY\n");
    advance(c, h, 260);
    CHECK(h.usbEn && h.usbTarget && c.usbToTarget());
    CHECK(send(c, h, "STAT?\n") == "STAT PWRLED=0 HDDLED=0 USB=TARGET BUSY=0\n");

    // LED sense: HDD activity is held for 250 ms so flicker is visible to polling
    h.pwrLed = true; h.hddLed = true; c.tick(); h.hddLed = false;
    advance(c, h, 100);
    CHECK(send(c, h, "STAT?\n") == "STAT PWRLED=1 HDDLED=1 USB=TARGET BUSY=0\n");
    advance(c, h, 300);
    CHECK(send(c, h, "STAT?\n") == "STAT PWRLED=1 HDDLED=0 USB=TARGET BUSY=0\n");

    // garbage, binary noise (e.g. a CH9329 frame sent to the wrong port), overlong lines
    CHECK(send(c, h, "FORMAT C:\n") == "ERR UNKNOWN\n");
    std::string ch9329 = "\x57\xAB\x00\x01\x00\x03";
    CHECK(send(c, h, ch9329.c_str()) == "");
    CHECK(send(c, h, "\n") == "ERR UNKNOWN\n");       // printable remnant of the frame is rejected, nothing pressed
    CHECK(!h.pwr && !h.rst);
    std::string longLine(200, 'A'); longLine += "\n";
    CHECK(send(c, h, longLine.c_str()) == "ERR LINE_TOO_LONG\n");
    CHECK(send(c, h, "HHT?\n").rfind("HHT-BRIDGEBOX-CTRL", 0) == 0);  // recovers after overflow

    // sensor clip: thermistor maths and fan RPM
    float tc = 0;
    CHECK(BridgeController::thermistorToCelsius(2048, tc) && tc > 24.5f && tc < 25.5f);   // R = 10k -> 25 C
    CHECK(BridgeController::thermistorToCelsius(1000, tc) && tc > 50.0f && tc < 60.0f);   // hotter -> lower R
    CHECK(!BridgeController::thermistorToCelsius(5, tc) && !BridgeController::thermistorToCelsius(4090, tc));
    CHECK(send(c, h, "SENS?\n") == "SENS T1=NA T2=NA FAN1=0\n");
    h.rawT1 = 2048;
    for (int i = 0; i < 200; ++i) { h.now += 10; h.pulses += 1; c.tick(); }        // 2 s at 100 pulses/s = 3000 RPM (one full window)
    std::string sens = send(c, h, "SENS?\n");
    CHECK(sens.rfind("SENS T1=25.", 0) == 0 || sens.rfind("SENS T1=24.", 0) == 0);
    CHECK(sens.find("T2=NA") != std::string::npos && sens.find("FAN1=3000") != std::string::npos);

    // millis() wrap-around
    h.now = 0xFFFFFF00u;
    send(c, h, "PWR SHORT\n");
    advance(c, h, 300); CHECK(h.pwr);
    advance(c, h, 150); CHECK(!h.pwr);

    std::printf(failures ? "%d FAILED\n" : "controller logic: all checks passed\n", failures);
    return failures ? 1 : 0;
}
