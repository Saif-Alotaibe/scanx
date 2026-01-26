using System;
using System.Collections.Generic;
using System.Text;

namespace ScanX.Core.Models
{
    public class ScanSetting
    {
        public enum DPI
        {
            DPI_72 = 72,
            DPI_96 = 96,
            DPI_150 = 150,
            DPI_300 = 300,
        }

        public enum ColorModel
        {
            Grayscale = 2,
            Color = 1,
            BlackAndWhite = 4
        }

        public enum PageSize
        {
            A4 = 0,
            Letter = 1,
        }

        public DPI Dpi { get; set; }

        public ColorModel Color { get; set; }

        public int Threshold { get; set; }

        //for more info https://www.papersizes.org/a-sizes-in-pixels.htm
        public static (int width,int height) GetA4SizeByDpi(int dpiValue)
        {

            var dpi = (DPI)dpiValue;

            switch (dpi)
            {
                case DPI.DPI_72:

                    return (619, 876);

                case DPI.DPI_96:

                    return (794, 1123);

                case DPI.DPI_150:

                    return (1240, 1754);

                case DPI.DPI_300:

                    return (2480, 3508);

                default:

                    return (794,1123);
            }
        }

        public static int GetResolution(DPI dpi)
        {
            switch (dpi)
            {
                case DPI.DPI_72:

                    return 75;

                case DPI.DPI_96:

                    return 96;

                case DPI.DPI_150:

                    return 150;

                case DPI.DPI_300:

                    return 300;

                default:
                    return 150;
            }
        }

        public ScanSetting()
        {
            this.Color = ColorModel.Color;
            this.Dpi = DPI.DPI_300;
        }
    }
}
