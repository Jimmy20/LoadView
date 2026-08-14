namespace LoadView
{
    internal enum SensorKind
    {
        Temperature,   // Value is degrees Celsius
        Fan            // Value is RPM
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
    }
}
