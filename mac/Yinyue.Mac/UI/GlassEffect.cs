using System.Runtime.InteropServices;
using AppKit;
using CoreGraphics;
using ObjCRuntime;

namespace Yinyue.UI
{
    /// <summary>
    /// Liquid Glass behind the overlay, reached through the Objective-C runtime.
    ///
    /// <b>Why the interop.</b> <c>NSGlassEffectView</c> is macOS 26, and the .NET 8 macOS
    /// workload builds against the macOS 15 SDK — so the class is absent from the bindings
    /// but present at runtime. Verified on 26.2: it exposes <c>contentView</c>,
    /// <c>cornerRadius</c>, <c>style</c> and <c>tintColor</c>. Calling it by selector is the
    /// ordinary way to use an API the bindings have not caught up with, and it degrades to
    /// nothing on an older system rather than failing to launch.
    ///
    /// <b>Why it is off by default.</b> A backdrop material resamples what is behind it
    /// whenever that changes — a real, ongoing GPU cost, and the sort of thing requirement 4
    /// exists to refuse. What makes it defensible at all is that this overlay is hidden
    /// almost all day and auto-hides after ten seconds, so the cost is bounded by the seconds
    /// it is on screen. That is an argument for offering it, not for switching it on.
    /// </summary>
    public static class GlassEffect
    {
        private const string ObjC = "/usr/lib/libobjc.dylib";

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendPtr(IntPtr receiver, IntPtr selector, IntPtr value);

        /// <summary>
        /// A separate declaration because a floating-point argument is passed in an FP
        /// register on arm64: reusing the IntPtr overload would put the bits in the wrong
        /// place and set a garbage radius.
        /// </summary>
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendDouble(IntPtr receiver, IntPtr selector, double value);

        /// <summary>
        /// True when this machine can draw it. Both halves are checked: the OS version, and
        /// the class actually being there — a version check alone would be a guess about
        /// what a future macOS still ships.
        /// </summary>
        public static bool IsAvailable =>
            OperatingSystem.IsMacOSVersionAtLeast(26) && Class.GetHandle("NSGlassEffectView") != IntPtr.Zero;

        [DllImport(ObjC, EntryPoint = "object_getClass")]
        private static extern IntPtr GetClass(IntPtr obj);

        [DllImport(ObjC, EntryPoint = "class_getName")]
        private static extern IntPtr GetClassName(IntPtr cls);

        /// <summary>
        /// The Objective-C class name of a view.
        ///
        /// For the suite: whether glass engaged cannot be inferred from the setting, because
        /// an unavailable material falls through silently and looks identical from the
        /// outside. Asking the object what it is, is the only honest check.
        /// </summary>
        public static string ClassNameOf(NSObject view) =>
            Marshal.PtrToStringAnsi(GetClassName(GetClass(view.Handle))) ?? "?";

        /// <summary>
        /// Wraps <paramref name="content"/> in a glass view, or returns it unchanged when the
        /// material is unavailable. The caller does not branch; an older machine simply gets
        /// the ordinary tinted panel.
        /// </summary>
        public static NSView Wrap(NSView content, double cornerRadius)
        {
            if (!IsAvailable) return content;

            IntPtr cls = Class.GetHandle("NSGlassEffectView");
            if (cls == IntPtr.Zero) return content;

            IntPtr handle = Send(Send(cls, Selector.GetHandle("alloc")), Selector.GetHandle("init"));
            if (handle == IntPtr.Zero) return content;

            var glass = Runtime.GetNSObject<NSView>(handle);
            if (glass is null) return content;

            glass.Frame = content.Frame;
            glass.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;

            SendDouble(handle, Selector.GetHandle("setCornerRadius:"), cornerRadius);

            // The content is handed to the glass rather than layered over it, which is what
            // lets the material read the content's shape rather than just sitting behind a
            // rectangle.
            content.Frame = new CGRect(0, 0, content.Frame.Width, content.Frame.Height);
            SendPtr(handle, Selector.GetHandle("setContentView:"), content.Handle);

            return glass;
        }
    }
}
