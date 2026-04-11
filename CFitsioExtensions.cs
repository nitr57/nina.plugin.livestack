using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack {

    internal class CFitsioExtensions {
        private const int FLEN_KEYWORD = 75;
        private const int FLEN_COMMENT = 73;

        [DllImport("cfitsionative.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "ffdkinit")]
        public static extern int fits_create_diskfile(out nint fptr, [MarshalAs(UnmanagedType.LPStr)] string filename, out int status);

        [DllImport("cfitsionative.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ffgpxv")]
        public static extern int fits_read_pix(nint fptr, NINA.Image.FileFormat.FITS.CfitsioNative.DATATYPE datatype, IntPtr firstpix, long nelem, nint nulval, nint array, out int anynul, out int status);

        // fits_read_key_double      ffgkyd
        [DllImport("cfitsionative.dll", CharSet = CharSet.Ansi, EntryPoint = "ffgkyd", CallingConvention = CallingConvention.Cdecl)]
        private static extern int _fits_read_key_double(
            IntPtr fptr,
            [MarshalAs(UnmanagedType.LPStr, SizeConst = FLEN_KEYWORD)] string keyname,
            out double value,
            [MarshalAs(UnmanagedType.LPStr, SizeConst = FLEN_COMMENT)] StringBuilder comm,
            out int status);

        public static double fits_read_key_double(IntPtr fptr, string keyname) {
            _fits_read_key_double(fptr, keyname, out var value, null, out var status);
            NINA.Image.FileFormat.FITS.CfitsioNative.CheckStatus("fits_read_key_long", status);
            return value;
        }

        // fits_read_key_double      ffgkye
        [DllImport("cfitsionative.dll", CharSet = CharSet.Ansi, EntryPoint = "ffgkye", CallingConvention = CallingConvention.Cdecl)]
        private static extern int _fits_read_key_float(
            IntPtr fptr,
            [MarshalAs(UnmanagedType.LPStr, SizeConst = FLEN_KEYWORD)] string keyname,
            out float value,
            [MarshalAs(UnmanagedType.LPStr, SizeConst = FLEN_COMMENT)] StringBuilder comm,
            out int status);

        public static float fits_read_key_float(IntPtr fptr, string keyname) {
            _fits_read_key_float(fptr, keyname, out var value, null, out var status);
            NINA.Image.FileFormat.FITS.CfitsioNative.CheckStatus("fits_read_key_long", status);
            return value;
        }

        // fits_read_key_double      ffgkyj
        [DllImport("cfitsionative.dll", CharSet = CharSet.Ansi, EntryPoint = "ffgkyj", CallingConvention = CallingConvention.Cdecl)]
        private static extern int _fits_read_key_lng(
            IntPtr fptr,
            [MarshalAs(UnmanagedType.LPStr, SizeConst = FLEN_KEYWORD)] string keyname,
            out long value,
            [MarshalAs(UnmanagedType.LPStr, SizeConst = FLEN_COMMENT)] StringBuilder comm,
            out int status);

        public static long fits_read_key_lng(IntPtr fptr, string keyname) {
            _fits_read_key_lng(fptr, keyname, out var value, null, out var status);
            NINA.Image.FileFormat.FITS.CfitsioNative.CheckStatus("fits_read_key_long", status);
            return value;
        }
    }
}