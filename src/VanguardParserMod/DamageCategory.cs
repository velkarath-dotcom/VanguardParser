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

    public class ShipDamageCategory
    {
        private double damageForDps;
        private DateTime? firstDamageUtc;

        public String ShipName { get; set; }
        public int Id { get; set; }
        public String Name => ShipName;
        public double Dps { get; private set; }
        public List<DamageCategory> DamageCategories { get; }

        public ShipDamageCategory(String shipName)
            : this(shipName, 0)
        {
        }

        public ShipDamageCategory(String shipName, int id)
        {
            ShipName = shipName;
            Id = id;
            DamageCategories = new List<DamageCategory>();
        }

        public ShipDamageCategory(String shipName, IEnumerable<DamageCategory> damageCategories)
            : this(shipName, 0, damageCategories)
        {
        }

        public ShipDamageCategory(String shipName, int id, IEnumerable<DamageCategory> damageCategories)
        {
            ShipName = shipName;
            Id = id;
            DamageCategories = new List<DamageCategory>(damageCategories ?? Enumerable.Empty<DamageCategory>());
        }

        public void AddDamageCategory(DamageCategory category)
        {
            if (category == null)
            {
                throw new ArgumentNullException(nameof(category));
            }

            DamageCategories.Add(category);
        }

        public void RecordDamage(double damage, DateTime timestampUtc)
        {
            damageForDps += damage;
            firstDamageUtc ??= timestampUtc;
            var elapsedSeconds = Math.Max((timestampUtc - firstDamageUtc.Value).TotalSeconds, 0.001d);
            Dps = damageForDps / elapsedSeconds;
        }

        public double GetTotalDamage()
        {
            double total = 0d;
            foreach (var category in DamageCategories)
            {
                total += category.GetTotalDamage();
            }

            return total;
        }

        public double GetTotalResistedDamage()
        {
            double total = 0d;
            foreach (var category in DamageCategories)
            {
                total += category.GetTotalResistedDamage();
            }

            return total;
        }
    }