    using System.Collections.Generic;
    using Source.Combat;
    namespace VGParserMod;
    public class DamageCategory
    {
        public String Name { get; set; }
        public double BaseTotal { get; set; }
        public double ResistedDamage { get; set; }
        public DamageType BaseDamageType { get; set; }
        public Dictionary<string, DamageCategory> ExtraDamage { get; }
        public double maxHit { get; set; }
        public double minHit { get; set; }
        public int hitCount { get; set; }
        public int CritCount { get; set; }
        public double CritPercentage => hitCount > 0 ? (CritCount / (double)hitCount) : 0d;
        public double dps { get; set; }

        public double GetTotalDamage()
        {
            var total = BaseTotal;
            foreach (var extraDamage in ExtraDamage.Values)
            {
                total += extraDamage.GetTotalDamage();
            }
            return total;
        }

        public double GetTotalResistedDamage()
        {
            var total = ResistedDamage;
            foreach (var extraDamage in ExtraDamage.Values)
            {
                total += extraDamage.GetTotalResistedDamage();
            }
            return total;
        }

        public double GetResistedDamagePercentage()
        {
            var originalTotal = GetTotalDamage() + GetTotalResistedDamage();
            return originalTotal > 0d ? GetTotalResistedDamage() / originalTotal : 0d;
        }

        public DamageCategory(String name, DamageType baseDamageType)
        {
            this.Name = name;
            this.BaseTotal = 0d;
            this.BaseDamageType = baseDamageType;
            this.ExtraDamage = new Dictionary<string, DamageCategory>();
            this.maxHit = 0d;
            this.minHit = 0d;
            this.hitCount = 0;
            this.CritCount = 0;
            this.dps = 0d;
        }
        public void AddExtraDamage(DamageType type, double damage, double resistedDamage, int critCount, double elapsedSeconds)
        {
            var name = $"Extra {type} Damage";
            if (!ExtraDamage.TryGetValue(name, out var extraDamage))
            {
                extraDamage = new DamageCategory(name, type);
                ExtraDamage[name] = extraDamage;
            }

            extraDamage.BaseTotal += damage;
            extraDamage.ResistedDamage += resistedDamage;
            extraDamage.hitCount++;
            extraDamage.CritCount += critCount;
            extraDamage.maxHit = Math.Max(extraDamage.maxHit, damage);
            extraDamage.minHit = extraDamage.minHit == 0d ? damage : Math.Min(extraDamage.minHit, damage);
            extraDamage.dps = extraDamage.GetTotalDamage() / Math.Max(elapsedSeconds, 0.001d);
        }
    }