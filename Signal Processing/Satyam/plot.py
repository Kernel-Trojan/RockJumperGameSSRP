import pandas as pd
import numpy as np
import matplotlib.pyplot as plt
df=pd.read_csv("Satyam.csv")

plt.plot(df["time_ms"],df["voltage_V"])
plt.plot(df["time_ms"],df["voltage_V"].rolling(window=3).median().rolling(window=8).mean())
plt.show()