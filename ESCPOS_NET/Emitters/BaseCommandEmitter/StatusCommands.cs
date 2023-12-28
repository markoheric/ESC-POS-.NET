using ESCPOS_NET.Emitters.BaseCommandValues;

namespace ESCPOS_NET.Emitters
{
    public abstract partial class BaseCommandEmitter : ICommandEmitter
    {
        /* Status Commands */
        public virtual byte[] EnableAutomaticStatusBack() => new byte[] { Cmd.GS, Status.AutomaticStatusBack, 0xFF };

        public virtual byte[] EnableAutomaticInkStatusBack() => new byte[] { Cmd.GS, Status.AutomaticInkStatusBack, 0xFF };
        public virtual byte[] DisableAutomaticStatusBack() => new byte[] { Cmd.GS, Status.AutomaticStatusBack, 0x00 };

        public virtual byte[] DisableAutomaticInkStatusBack() => new byte[] { Cmd.GS, Status.AutomaticInkStatusBack, 0x00 };
        public virtual byte[] RequestPaperStatus() => new byte[] { Cmd.GS, Status.RequestStatus, StatusCommand.PaperStatus };
        public virtual byte[] RequestDrawerStatus() => new byte[] { Cmd.GS, Status.RequestStatus, StatusCommand.DrawerStatus };
        public virtual byte[] RequestInkStatus() => new byte[] { Cmd.GS, Status.RequestStatus, StatusCommand.InkStatus };
        public virtual byte[] RequestSerialNumber() => new byte[] { Cmd.GS, Status.RequestStatus, StatusCommand.SerialNumber };
    }

    public sealed class StatusCommand
    {
        public static readonly byte PaperStatus = 0x31;
        public static readonly byte DrawerStatus = 0x32;
        public static readonly byte InkStatus = 0x34;
        public static readonly byte SerialNumber = 0x35;
        //public static readonly byte AutomaticStatusBack = 0x61;
        //public static readonly byte AutomaticInkStatusBack = 0x6A;

    }
}
