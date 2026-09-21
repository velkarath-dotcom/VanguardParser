    using Source.Combat;
    namespace VGParserMod;
    public class DamageCategory
    {
        public String Name { get; set; }
        public double BaseTotal { get; set; }
        public DamageType BaseDamageType { get; set; }
        public double ExtraHeat { get; set; }
        public double ExtraCold { get; set; }
        public double ExtraEnergy { get; set; }
        public double ExtraKinetic { get; set; }
        public double ExtraRadiation { get; set; }
        public double ExtraCorrosion { get; set; }
        public double ExtraExplosive { get; set; }
        public double maxHit { get; set; }
        public double minHit { get; set; }
        public int hitCount { get; set; }
        public double dps { get; set; }

        public double GetTotalDamage()
        {
            return BaseTotal + ExtraHeat + ExtraCold + ExtraEnergy + ExtraKinetic + ExtraRadiation + ExtraCorrosion + ExtraExplosive;
        }
        public DamageCategory(String name, DamageType baseDamageType)
        {
            this.Name = name;
            this.BaseTotal = 0d;
            this.BaseDamageType = baseDamageType;
            this.maxHit = 0d;
            this.minHit = 0d;
            this.hitCount = 0;
            this.ExtraHeat = 0d;
            this.ExtraCold = 0d;
            this.ExtraEnergy = 0d;
            this.ExtraKinetic = 0d;
            this.ExtraRadiation = 0d;
            this.ExtraCorrosion = 0d;
            this.ExtraExplosive = 0d;
            this.dps = 0d;
        }
        public void AddExtraDamage(DamageType type, double damage)
        {
            switch (type)
            {
                case DamageType.Kinetic:
                    this.ExtraKinetic += damage;
                    break;
                case DamageType.Energy:
                    this.ExtraEnergy += damage;
                    break;
                case DamageType.Radiation:
                    this.ExtraRadiation += damage;
                    break;
                case DamageType.Heat:
                    this.ExtraHeat += damage;
                    break;
                case DamageType.Cold:
                    this.ExtraCold += damage;
                    break;
                case DamageType.Corrosion:
                    this.ExtraCorrosion += damage;
                    break;
                case DamageType.Explosive:
                    this.ExtraExplosive += damage;
                    break;
                default:
                    throw new NotImplementedException("Nieuwe DamageType niet volledig geimplementeerd: " + type.ToString());
            }
        }
    }