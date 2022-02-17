namespace Lykke.Job.SiriusDepositsDetector.Utils
{
    public static class NumericExtensions
    {
        public static int GetScale(this decimal value)
        {
            uint[] bits = (uint[])(object)decimal.GetBits(value);

            uint scale = (bits[3] >> 16) & 31;

            return (int)scale;
        }
    }
}
