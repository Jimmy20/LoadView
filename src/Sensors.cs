using System;

namespace LoadView
{
    internal enum SensorKind
    {
        Temperature,   // Value is degrees Celsius
        Fan            // Value is RPM
    }

    // What kind of component a reading came from. Only used to decide "is this hot?", which is a
    // different question per component: 70 °C is an idle afternoon for a CPU and a bad day for a
    // hard disk, so one threshold across everything can only ever be wrong for something.
    internal enum SensorClass
    {
        Cpu,
        Gpu,
        Disk,
        Other          // chipset, motherboard, whatever else the driver exposes
    }

    // One reading from one component. Id is stable across restarts and is what the user's tile
    // selection is stored against — never an index, because an index moves when hardware is added.
    internal struct SensorReading
    {
        public string Id;
        public string Label;    // short, for a tile: "CPU", "C:", "Chipset"
        public string Detail;   // longer, for the settings list: the disk model, the chip name
        public SensorKind Kind;
        public double Value;

        // Derived from Id, which is assigned where the reading is produced: "cpu", "gpu",
        // "disk:<serial>", and for driver-provided sensors the library's own identifier
        // ("/lpc/nct6687d/temperature/2"), which is neither of the first three.
        public SensorClass Class
        {
            get
            {
                if (string.IsNullOrEmpty(Id)) return SensorClass.Other;
                if (Id == "cpu") return SensorClass.Cpu;
                if (Id == "gpu" || Id.StartsWith("gpu:", StringComparison.Ordinal)) return SensorClass.Gpu;
                if (Id.StartsWith("disk:", StringComparison.Ordinal)) return SensorClass.Disk;
                return SensorClass.Other;
            }
        }
    }
}
