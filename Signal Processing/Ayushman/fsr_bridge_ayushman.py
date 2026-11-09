#!/usr/bin/env python3
"""
fsr_bridge_ayushman.py  -  live FSR filter for the pinch-jump game (Ayushman's design).

  Arduino (COM5)  ->  this script  ->  Unity (UDP, same PC)

What it does with every reading, in this order (the design of "Ayushman's FSR data: signal
analysis and filter design", Sections 5 and 7):

  1. Block 1: median of the last 3 readings            (removes one-sample spikes and dropouts)
  2. Block 2: second-order lowpass, double pole at z = 0.7, double zero at z = -1   (removes jitter and noise)
       y[n] = 1.4 y[n-1] - 0.49 y[n-2] + 0.0225 (x[n] + 2 x[n-1] + x[n-2])
  3. Rest level: measured once at start-up with the finger OFF, then subtracted
  4. Press starts when the output is 10 mV above rest, press is over when it falls to 5 mV or less
  5. While pressed, the highest output value is remembered. When the press is over, that peak is sent to
     Unity and the player jumps.

Every block only uses readings that have already arrived, so it runs live.
The design assumes about 200 readings per second (5 ms apart): use fsr_live.ino on the Arduino.

Usage:   python fsr_bridge_ayushman.py COM5
         python fsr_bridge_ayushman.py COM5 5005        (second number = UDP port, must match Unity)

While it runs you can type   z   and press Enter to measure the rest level again (finger OFF).
Stop with Ctrl+C. Close the Arduino Serial Monitor first: only one program can use COM5.
"""

import sys
import csv
import time
import socket
import statistics
import threading
from collections import deque
from datetime import datetime

import serial


# ============================================================
# 1. SETTINGS
# ============================================================

BAUD_RATE = 115200
SERIAL_TIMEOUT = 0.1

UDP_HOST = "127.0.0.1"
UDP_PORT = 5005

VREF = 5.0                 # Arduino reference voltage (3.3 for a 3.3 V board)
ADC_MAX = 1023
USE_ADC_SUM4 = True        # use the adc_sum4 column when it exists (more exact than the rounded voltage)

# --- the filter block (PDF Section 5) ---
EXPECTED_FS_HZ = 200.0     # the design assumes this many readings per second
MEDIAN_WINDOW = 3          # Block 1
LP_POLE = 0.7               # Block 2: double pole at z = LP_POLE (0.7 = 3 dB at 7.2 Hz, 30 ms delay at 200 Hz)
                           #          0.75 = a little less jitter but 10 ms more delay

# --- rest level (PDF Section 7) ---
REST_TIME_S = 3.0          # keep the finger OFF the sensor for this long after starting
REST_MAX_RANGE_V = 0.030   # if the output moves more than this during that time, the finger was probably on: measure again
WARMUP_S = 0.3             # ignore the first moments of the filters before measuring the rest level

# --- press detection (PDF Section 7), all in volts ABOVE the rest level ---
PRESS_DELTA_V = 0.010      # press starts at 10 mV above rest
RELEASE_DELTA_V = 0.005    # press is over at 5 mV above rest or lower
MIN_PEAK_DELTA_V = 0.010   # a press whose peak stays below this is ignored
MERGE_GAP_MS = 25.0        # a dip below the release level shorter than this does NOT end the press.
                           # 0 = jump at the first reading below it (fastest, but a wobble can give a double jump)


# ============================================================
# 2. READING ARDUINO LINES
# ============================================================

class LineParser:
    """Understands '# info' lines (ignored), a header like 'time_ms,voltage_V,adc_sum4', and the rows."""

    def __init__(self):
        self.columns = None

    def parse(self, line):
        """Returns (arduino_time_ms or None, volts) or None."""
        line = line.strip()
        if not line or line.startswith("#"):
            return None

        fields = next(csv.reader([line]))
        try:
            values = [float(x.strip()) for x in fields]
        except ValueError:
            names = [x.strip().lower() for x in fields]
            if any(n in ("time_ms", "voltage_v", "adc_sum4") for n in names):
                self.columns = {n: i for i, n in enumerate(names)}
                print("Arduino header:", fields)
            return None

        t_ms = None
        volts = None

        if self.columns is not None:
            ti = self.columns.get("time_ms")
            vi = self.columns.get("voltage_v")
            ai = self.columns.get("adc_sum4")
            if ti is not None and ti < len(values):
                t_ms = values[ti]
            if USE_ADC_SUM4 and ai is not None and ai < len(values):
                volts = values[ai] / 4.0 * VREF / ADC_MAX
            elif vi is not None and vi < len(values):
                volts = values[vi]
        else:
            # no header seen (for example the Arduino was already running): time_ms,voltage[,adc_sum4]
            if len(values) >= 3:
                t_ms = values[0]
                volts = values[2] / 4.0 * VREF / ADC_MAX if USE_ADC_SUM4 else values[1]
            elif len(values) == 2:
                t_ms, volts = values[0], values[1]
            else:
                volts = values[0]

        if volts is None or volts != volts or volts in (float("inf"), float("-inf")):
            return None
        return t_ms, volts


# ============================================================
# 3. THE FILTER BLOCK + PRESS DETECTION
# ============================================================

IDLE, PRESSED, RELEASING, CALIBRATING = 0, 1, 2, 3
STATE_NAMES = {IDLE: "idle", PRESSED: "pressed", RELEASING: "releasing", CALIBRATING: "calibrating"}


class FsrProcessor:
    def __init__(self):
        self.median_buf = deque(maxlen=MEDIAN_WINDOW)
        # Block 2 memory: the last two inputs (x1, x2) and the last two outputs (y1, y2)
        self.lp_x1 = self.lp_x2 = 0.0
        self.lp_y1 = self.lp_y2 = 0.0
        self.primed = False      # the filters start from the first reading, not from zero

        self.rest = None
        self.cal_values = []
        self.cal_start = None

        self.state = CALIBRATING
        self.peak = 0.0
        self.last_above = 0.0
        self.release_count = 0
        self.last_peak = 0.0

        self.prev_t = None
        self.dt_sum = 0.0
        self.dt_n = 0
        self.rate_checked = False

        self.events = []     # (name, time_ms, value) produced by the latest process() call

    # ---- Block 1 ----
    def median_block(self, x):
        self.median_buf.append(x)
        return statistics.median(self.median_buf)

    # ---- Block 2 ----
    def lowpass_block(self, x):
        """Second-order pole-zero lowpass: double pole at LP_POLE, double zero at -1, gain 1 at 0 Hz.
        y[n] = 2p y[n-1] - p^2 y[n-2] + K (x[n] + 2 x[n-1] + x[n-2]),   K = (1 - p)^2 / 4
        With p = 0.7 this is y[n] = 1.4 y[n-1] - 0.49 y[n-2] + 0.0225 (x[n] + 2 x[n-1] + x[n-2])."""
        p = LP_POLE
        k = (1.0 - p) ** 2 / 4.0
        y = 2.0 * p * self.lp_y1 - p * p * self.lp_y2 + k * (x + 2.0 * self.lp_x1 + self.lp_x2)
        self.lp_x2, self.lp_x1 = self.lp_x1, x
        self.lp_y2, self.lp_y1 = self.lp_y1, y
        return y

    def prime(self, x):
        """Start every block at the first reading, so the output does not climb up from zero at start-up."""
        self.median_buf.clear()
        for _ in range(MEDIAN_WINDOW):
            self.median_buf.append(x)
        self.lp_x1 = self.lp_x2 = x
        self.lp_y1 = self.lp_y2 = x
        self.primed = True

    def filter_chain(self, x):
        """One raw reading in -> (output of Block 1, output of the whole block)."""
        if not self.primed:
            self.prime(x)
        y1 = self.median_block(x)
        y2 = self.lowpass_block(y1)
        return y1, y2

    def recalibrate(self):
        """Measure the rest level again (finger OFF)."""
        self.rest = None
        self.cal_values = []
        self.cal_start = None
        self.state = CALIBRATING
        self.peak = 0.0

    def reset_filters(self):
        self.primed = False
        self.prev_t = None
        self.recalibrate()

    def _check_rate(self, t_ms):
        if self.prev_t is not None and t_ms > self.prev_t:
            self.dt_sum += t_ms - self.prev_t
            self.dt_n += 1
        if not self.rate_checked and self.dt_n >= 400:
            self.rate_checked = True
            fs = 1000.0 * self.dt_n / self.dt_sum
            if abs(fs - EXPECTED_FS_HZ) > 0.25 * EXPECTED_FS_HZ:
                print(f"\nWARNING: the Arduino sends {fs:.0f} readings per second, but this filter is designed for "
                      f"about {EXPECTED_FS_HZ:.0f}.\n"
                      f"         At {fs:.0f} Hz the lowpass corner moves to about {7.2 * fs / EXPECTED_FS_HZ:.1f} Hz "
                      f"and its delay to {33.0 * EXPECTED_FS_HZ / fs:.0f} ms.\n"
                      f"         Upload fsr_live.ino (200 Hz).\n")
            else:
                print(f"Reading rate: {fs:.1f} Hz (filter designed for about {EXPECTED_FS_HZ:.0f} Hz) - OK")

    def process(self, t_ms, volts):
        """One reading in, one result out."""
        self.events = []

        # the Arduino restarted (its clock went backwards) or was silent for 2 s: start fresh
        if self.prev_t is not None and (t_ms < self.prev_t or t_ms - self.prev_t > 2000.0):
            print("Arduino restarted or was silent - measuring the rest level again.")
            self.reset_filters()
        self._check_rate(t_ms)
        self.prev_t = t_ms

        y1, y2 = self.filter_chain(volts)

        if self.state == CALIBRATING:
            self._calibrate(y2, t_ms)
            delta = 0.0
        else:
            delta = y2 - self.rest
            self._detect(delta, t_ms)

        return {
            "median": y1,
            "filtered": y2,
            "delta": delta,
            "state": self.state,
            "release_count": self.release_count,
            "last_peak": self.last_peak,
            "cur_peak": self.peak if self.state in (PRESSED, RELEASING) else 0.0,
        }

    def _calibrate(self, y2, t_ms):
        if self.cal_start is None:
            self.cal_start = t_ms
        # skip the first moments of the filters
        if t_ms - self.cal_start >= 1000.0 * WARMUP_S:
            self.cal_values.append(y2)

        if t_ms - self.cal_start < REST_TIME_S * 1000.0:
            return

        if len(self.cal_values) < 20:
            self.cal_values = []
            self.cal_start = t_ms
            return

        rng = max(self.cal_values) - min(self.cal_values)
        if rng > REST_MAX_RANGE_V:
            print(f"Rest measurement moved by {rng * 1000:.1f} mV - was the finger on the sensor? Measuring again. "
                  f"Keep it OFF.")
            self.cal_values = []
            self.cal_start = t_ms
            return

        self.rest = statistics.median(self.cal_values)
        sd = statistics.pstdev(self.cal_values)
        self.state = IDLE
        print("\n========== REST LEVEL MEASURED ==========")
        print(f"Rest level      : {self.rest * 1000:.1f} mV")
        print(f"Noise (SD)      : {sd * 1000:.2f} mV")
        print(f"Largest wobble  : {rng * 1000:.1f} mV (peak to peak)")
        print(f"Press starts at : rest + {PRESS_DELTA_V * 1000:.0f} mV = {(self.rest + PRESS_DELTA_V) * 1000:.1f} mV")
        print(f"Press is over at: rest + {RELEASE_DELTA_V * 1000:.0f} mV = {(self.rest + RELEASE_DELTA_V) * 1000:.1f} mV")
        if self.rest > 0.15:
            print("WARNING: the rest level is high. Is something pressing on the sensor? (type z to measure again)")
        print("==========================================\n")

    def _detect(self, d, t):
        if self.state == IDLE:
            if d >= PRESS_DELTA_V:
                self.state = PRESSED
                self.peak = d
                self.last_above = t
                self.events.append(("PRESS", t, d))

        elif self.state == PRESSED:
            if d > self.peak:
                self.peak = d
            if d > RELEASE_DELTA_V:
                self.last_above = t
            else:
                self.state = RELEASING

        # not "elif": the same reading may have just dropped to the release level
        if self.state == RELEASING:
            if d > RELEASE_DELTA_V:
                # came back up before the gap ran out: still the same press
                self.state = PRESSED
                self.last_above = t
                if d > self.peak:
                    self.peak = d
            elif t - self.last_above > MERGE_GAP_MS:
                self.state = IDLE
                if self.peak >= MIN_PEAK_DELTA_V:
                    self.release_count += 1
                    self.last_peak = self.peak
                    self.events.append(("RELEASE", t, self.peak))
                else:
                    self.events.append(("IGNORED", t, self.peak))


# ============================================================
# 4. MAIN PROGRAM
# ============================================================

def main():
    global UDP_PORT

    if len(sys.argv) < 2:
        print("Usage:")
        print("  python fsr_bridge_ayushman.py COM5")
        print("  python fsr_bridge_ayushman.py COM5 5005")
        sys.exit(1)

    port_name = sys.argv[1]
    if len(sys.argv) > 2:
        UDP_PORT = int(sys.argv[2])

    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    samples_file = f"fsr_bridge_{stamp}.csv"
    events_file = f"fsr_bridge_events_{stamp}.csv"

    try:
        ser = serial.Serial(port_name, BAUD_RATE, timeout=SERIAL_TIMEOUT)
    except serial.SerialException as e:
        print("Could not open", port_name, ":", e)
        print("Is the Arduino Serial Monitor (or another program) still using it?")
        sys.exit(1)

    time.sleep(2)                 # the Uno restarts when the port opens
    ser.reset_input_buffer()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    proc = FsrProcessor()
    parser = LineParser()

    print(f"\nConnected to {port_name}.  Sending to Unity at {UDP_HOST}:{UDP_PORT}")
    print(f"Saving to {samples_file} and {events_file}")
    print(f"Keep the finger OFF the sensor for the first {REST_TIME_S:.0f} seconds.")
    print("Type z + Enter to measure the rest level again. Ctrl+C to stop.\n")

    recal_requested = threading.Event()

    def command_thread():
        while True:
            try:
                cmd = input()
            except (EOFError, OSError):
                break
            if cmd.strip().lower() == "z":
                recal_requested.set()

    threading.Thread(target=command_thread, daemon=True).start()

    rows = 0
    last_flush = time.time()
    last_status = 0
    t0_host = time.monotonic()

    try:
        with open(samples_file, "w", newline="") as sf, open(events_file, "w", newline="") as ef:
            sw = csv.writer(sf)
            ew = csv.writer(ef)
            sw.writerow(["Unix_Time_s", "Arduino_Time_ms", "Raw_Voltage_V", "Median_V", "Filtered_V",
                         "Above_Rest_V", "State", "Current_Peak_V"])
            ew.writerow(["Unix_Time_s", "Event", "Value_V"])

            while True:
                line = ser.readline().decode(errors="ignore")
                if not line:
                    continue
                parsed = parser.parse(line)
                if parsed is None:
                    continue

                t_ms, volts = parsed
                now = time.time()
                if t_ms is None:                       # no Arduino clock: use ours
                    t_ms = (time.monotonic() - t0_host) * 1000.0

                if recal_requested.is_set():
                    recal_requested.clear()
                    print("Measuring the rest level again - keep the finger OFF.")
                    proc.recalibrate()

                r = proc.process(t_ms, volts)

                for name, ev_t, value in proc.events:
                    ew.writerow([f"{now:.3f}", name, f"{value:.5f}"])
                    if name == "PRESS":
                        print(f"[PRESS]   {value * 1000:6.1f} mV above rest")
                    elif name == "RELEASE":
                        print(f"[RELEASE] peak {value * 1000:6.1f} mV above rest  ->  jump")
                    else:
                        print(f"[IGNORED] peak {value * 1000:6.1f} mV (below the minimum)")

                packet = "S,%.3f,%.1f,%.5f,%.5f,%.5f,%d,%d,%.5f,%.5f\n" % (
                    now, t_ms, volts, r["filtered"], r["delta"], r["state"],
                    r["release_count"], r["last_peak"], r["cur_peak"])
                try:
                    sock.sendto(packet.encode("ascii"), (UDP_HOST, UDP_PORT))
                except OSError:
                    pass                               # Unity is not listening yet: keep going

                sw.writerow([f"{now:.4f}", f"{t_ms:.0f}", f"{volts:.5f}", f"{r['median']:.5f}",
                             f"{r['filtered']:.5f}", f"{r['delta']:.5f}", r["state"], f"{r['cur_peak']:.5f}"])
                rows += 1

                if now - last_flush > 1.0:
                    last_flush = now
                    sf.flush()
                    ef.flush()

                if rows - last_status >= 400:
                    last_status = rows
                    print(f"  readings {rows} | raw {volts * 1000:6.1f} mV | filtered {r['filtered'] * 1000:6.1f} mV | "
                          f"above rest {r['delta'] * 1000:6.1f} mV | {STATE_NAMES[r['state']]}")

    except KeyboardInterrupt:
        print("\nStopping...")
    except (serial.SerialException, OSError) as e:
        print("\nSerial error:", e)
    finally:
        if ser.is_open:
            ser.close()
        print(f"\nReadings: {rows}\nSaved: {samples_file}, {events_file}")


if __name__ == "__main__":
    main()
