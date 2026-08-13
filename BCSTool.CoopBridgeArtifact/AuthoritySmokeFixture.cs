namespace AuthoritySmokeFixture
{
    public static class Target
    {
        public static int Count { get; private set; }
        public static int ClientOnlyCount { get; private set; }

        public static void Reset()
        {
            Count = 0;
            ClientOnlyCount = 0;
        }

        public static void Invoke()
        {
            Mutate();
        }

        public static void InvokeClientOnly()
        {
            MutateClientOnly();
        }

        private static void Mutate()
        {
            Count++;
        }

        private static void MutateClientOnly()
        {
            ClientOnlyCount++;
        }
    }
}
