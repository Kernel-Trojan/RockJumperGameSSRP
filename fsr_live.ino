// FSR -> USB serial for the LIVE game and for fsr_realtime_filter.py.
// Prints this header once, then 200 lines per second:
//   time_ms,voltage_v,adc_sum4
//   time_ms   = milliseconds since the Arduino started
//   voltage_v = the sensor voltage in volts (average of 4 readings)
//   adc_sum4  = the 4 ADC readings added together (0..4092)
// Nothing else is filtered here: the median filter, low-pass filter and thresholds run in Unity
// (or in fsr_realtime_filter.py), exactly as in your report.
//
// If your Arduino already runs a sketch that prints these three columns, you do NOT need this one.
//
// Wiring A (normal):         5V -> sensor -> A0 -> 10k resistor -> GND      (voltage rises when you press)  -> PULLUP_ONLY = false
// Wiring B (no resistor):    sensor between A0 and GND, nothing else         (uses the built-in resistor)    -> PULLUP_ONLY = true

const int   FSR_PIN     = A0;
const float VREF        = 5.0;                // use 3.3 for a 3.3 V board
const int   ADC_MAX     = 1023;
const bool  PULLUP_ONLY = false;
const unsigned long PERIOD_US = 5000;         // 5000 us = 200 readings per second (the Python script assumes about 200)

unsigned long nextTime;

void setup() {
  Serial.begin(115200);                       // must match the baud rate in Unity
  if (PULLUP_ONLY) pinMode(FSR_PIN, INPUT_PULLUP);
  Serial.println(F("time_ms,voltage_v,adc_sum4"));
  nextTime = micros();
}

void loop() {
  unsigned long now = micros();
  if ((long)(now - nextTime) < 0) return;     // not time for the next reading yet
  nextTime += PERIOD_US;                      // fixed schedule: no drift

  long sum = 0;
  for (int i = 0; i < 4; i++) {
    int r = analogRead(FSR_PIN);
    if (PULLUP_ONLY) r = ADC_MAX - r;         // flip so a harder press gives a bigger number
    sum += r;
  }
  float volts = (sum / 4.0) * VREF / ADC_MAX;

  Serial.print(millis());
  Serial.print(',');
  Serial.print(volts, 5);
  Serial.print(',');
  Serial.println(sum);
}
