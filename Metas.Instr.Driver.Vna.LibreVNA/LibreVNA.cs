/*************************************************************************
 *
 *               File     : LibreVNA.cs
 *               Class    : LibreVNA
 *                          LibreVnaScpiSession
 *               Library  : Metas.Instr.Driver.Vna
 *               Version  : 1.0.0.0
 *               Author   : Lukas Ebner (TU Graz)
 *               Created  : 14.07.2026
 *
 *               Driver for the LibreVNA vector network analyzer.
 *
 *               The LibreVNA is controlled through the SCPI server of the
 *               LibreVNA-GUI (TCP, default port 19542). The LibreVNA-GUI
 *               must be running and the SCPI server must be enabled:
 *               Window -> Preferences -> General -> SCPI Control.
 *
 *               Resource name formats:
 *                 localhost
 *                 localhost:19542
 *                 192.168.1.10:19542
 *                 TCPIP0::localhost::19542::SOCKET
 *
 *               Notes:
 *               - No VISA required, plain TCP socket communication.
 *               - Sweep completion is detected by polling
 *                 VNA:ACQuisition:FINished? (no SRQ available on a raw
 *                 TCP socket).
 *               - VnaFormat.RawData requires that no calibration is active
 *                 in the LibreVNA-GUI (there is no SCPI command to
 *                 temporarily deactivate an active calibration without
 *                 deleting its measurements).
 *               - SetState/GetState use a temporary setup file. This only
 *                 works when the LibreVNA-GUI runs on the same machine as
 *                 METAS VNA Tools.
 *
 ************************************************************************/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using Metas.UncLib.Core;
using Metas.Vna.Data;

namespace Metas.Instr.Driver.Vna
{
    /// <summary>
    /// Driver for the LibreVNA vector network analyzer (hardware version 1),
    /// controlled through the SCPI server of the LibreVNA-GUI.
    /// Tested with LibreVNA-GUI v1.6.x SCPI command set.
    /// </summary>
    public class LibreVNA : IVna
    {
        /// <summary>
        /// SCPI TCP session to the LibreVNA-GUI.
        /// </summary>
        public LibreVnaScpiSession scpi;

        private VnaSetUpMode setupmode;
        internal VnaParameter[] parameters;
        private VnaParameter[,] parameterMatrix = null;

        /// <summary>
        /// Trace names in the LibreVNA-GUI used for each entry of
        /// <see cref="Parameters"/>.
        /// </summary>
        private string[] traceNames;

        /// <summary>
        /// Timeout for a complete sweep including averaging / ms.
        /// </summary>
        public int SweepTimeout = 7200000;

        /// <summary>
        /// Poll interval while waiting for sweep completion / ms.
        /// </summary>
        public int PollInterval = 100;

        #region Constructor

        /// <summary>
        /// Creates a VNA session.
        /// </summary>
        public LibreVNA()
        {
            parameters = new VnaParameter[0];
            traceNames = new string[0];
        }

        /// <summary>
        /// Creates a VNA session to the specified resource.
        /// </summary>
        /// <param name="resourceName">Host name, host:port or VISA style TCPIP socket resource of the LibreVNA-GUI SCPI server.</param>
        /// <param name="idName">String that describes the instrument identification.</param>
        public LibreVNA(string resourceName, string idName = null)
        {
            Open(resourceName, idName);
        }

        #endregion

        #region IDevice Members

        /// <summary>
        /// Opens a VNA session to the specified resource.
        /// </summary>
        /// <param name="resourceName">Host name, host:port or VISA style TCPIP socket resource of the LibreVNA-GUI SCPI server.</param>
        /// <param name="idName">String that describes the instrument identification.</param>
        public void Open(string resourceName, string idName = null)
        {
            setupmode = VnaSetUpMode.unknown;
            parameters = new VnaParameter[0];
            traceNames = new string[0];
            parameterMatrix = null;
            string host;
            int port;
            ParseResource(resourceName, out host, out port);
            scpi = new LibreVnaScpiSession(host, port);
            scpi.Timeout = 5000;
            string idn = Identification;
            if (idn.IndexOf("LibreVNA") < 0 &&
                (string.IsNullOrEmpty(idName) || idn.IndexOf(idName) < 0))
            {
                throw new Exception("Instrument identification query failed: " + idn);
            }
            // Connect to the first device if the GUI is not connected yet
            string serial = scpi.Query("DEVice:CONNect?");
            if (serial.IndexOf("Not connected") >= 0)
            {
                scpi.Write("DEVice:CONNect");
                Thread.Sleep(500);
                serial = scpi.Query("DEVice:CONNect?");
                if (serial.IndexOf("Not connected") >= 0)
                {
                    throw new Exception("LibreVNA-GUI is running but no LibreVNA device is connected.");
                }
            }
            Init();
        }

        private void Init()
        {
            // Make sure the device is in VNA mode and sweeping frequency
            string mode = scpi.Query("DEVice:MODE?");
            if (mode.IndexOf("VNA") < 0)
            {
                scpi.Write("DEVice:MODE VNA");
            }
            scpi.Write("VNA:SWEEP FREQUENCY");
        }

        private static void ParseResource(string resourceName, out string host, out int port)
        {
            host = "localhost";
            port = 19542;
            if (string.IsNullOrEmpty(resourceName)) return;
            string s = resourceName.Trim();
            if (s.ToUpperInvariant().StartsWith("TCPIP"))
            {
                // TCPIP[board]::host[::port]::SOCKET
                string[] p = s.Split(new string[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 2) host = p[1];
                if (p.Length >= 3)
                {
                    int tmp;
                    if (int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out tmp)) port = tmp;
                }
            }
            else
            {
                string[] p = s.Split(':');
                host = p[0];
                if (p.Length > 1)
                {
                    int tmp;
                    if (int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out tmp)) port = tmp;
                }
            }
        }

        /// <summary>
        /// Closes the specified device session.
        /// </summary>
        public void Close()
        {
            try
            {
                TriggerCont();
            }
            catch
            {
            }
            scpi.Dispose();
            scpi = null;
        }

        /// <summary>
        /// Instrument Identification
        /// </summary>
        public string Identification
        {
            get
            {
                return scpi.Query("*IDN?").Split('\n')[0];
            }
        }

        /// <summary>
        /// Presets the device.
        /// </summary>
        public void Preset()
        {
            setupmode = VnaSetUpMode.unknown;
            parameters = new VnaParameter[0];
            traceNames = new string[0];
            parameterMatrix = null;
            scpi.Write("*RST");
            Thread.Sleep(1000);
            Init();
        }

        #endregion

        #region ITriggerSingleDevice

        /// <summary>
        /// Trigger device.
        /// </summary>
        public void TriggerSingleStart()
        {
            // Re-writing the average setting forces the averaging level
            // (VNA:ACQ:AVGLEVel) back to zero, so that a subsequent
            // VNA:ACQ:FINished? query cannot return a stale TRUE from a
            // previous acquisition.
            int avg = IFAverageFactor;
            scpi.Write("VNA:ACQuisition:AVG " + avg.ToString(CultureInfo.InvariantCulture));
            // VNA:ACQ:SINGLE TRUE always triggers a new sweep
            scpi.Write("VNA:ACQuisition:SINGLE TRUE");
        }

        /// <summary>
        /// Waits for measurement complete.
        /// </summary>
        /// <param name="worker">Background Worker</param>
        /// <param name="e">Do Work Event Arguments</param>
        public void TriggerSingleWait(BackgroundWorker worker = null, DoWorkEventArgs e = null)
        {
            DateTime start = DateTime.Now;
            // Give the GUI time to restart the acquisition
            Thread.Sleep(PollInterval);
            while (true)
            {
                if (worker != null && worker.CancellationPending)
                {
                    if (e != null) e.Cancel = true;
                    scpi.Write("VNA:ACQuisition:STOP");
                    return;
                }
                bool running = scpi.QueryBoolean("VNA:ACQuisition:RUN?");
                bool finished = scpi.QueryBoolean("VNA:ACQuisition:FINished?");
                if (!running && finished)
                {
                    return;
                }
                if ((DateTime.Now - start).TotalMilliseconds > SweepTimeout)
                {
                    throw new TimeoutException("LibreVNA sweep did not complete within the sweep timeout.");
                }
                Thread.Sleep(PollInterval);
            }
        }

        /// <summary>
        /// Triggers device and waits for measurement complete.
        /// </summary>
        /// <param name="worker">Background Worker</param>
        /// <param name="e">Do Work Event Arguments</param>
        public void TriggerSingle(BackgroundWorker worker = null, DoWorkEventArgs e = null)
        {
            TriggerSingleStart();
            TriggerSingleWait(worker, e);
        }

        #endregion

        #region ITriggerSingleContHoldDevice

        /// <summary>
        /// Trigger continuous.
        /// </summary>
        public void TriggerCont()
        {
            scpi.Write("VNA:ACQuisition:SINGLE FALSE");
            scpi.Write("VNA:ACQuisition:RUN");
        }

        /// <summary>
        /// Trigger hold.
        /// </summary>
        public void TriggerHold()
        {
            scpi.Write("VNA:ACQuisition:STOP");
        }

        #endregion

        #region IVna Members

        private string TempSetupFile
        {
            get
            {
                return Path.Combine(Path.GetTempPath(), "MetasLibreVnaDriver.setup");
            }
        }

        /// <summary>
        /// Set instrument state to VNA.
        /// Note: this uses a temporary setup file and only works when the
        /// LibreVNA-GUI runs on the same machine as METAS VNA Tools.
        /// </summary>
        /// <param name="state">Instrument state</param>
        public void SetState(byte[] state)
        {
            setupmode = VnaSetUpMode.unknown;
            parameters = new VnaParameter[0];
            traceNames = new string[0];
            parameterMatrix = null;
            string path = TempSetupFile;
            File.WriteAllBytes(path, state);
            try
            {
                int t = scpi.Timeout;
                scpi.Timeout = 30000;
                string ok = scpi.Query("DEVice:SETUP:LOAD? " + path);
                scpi.Timeout = t;
                if (ok.IndexOf("TRUE") < 0)
                {
                    throw new Exception("LibreVNA-GUI failed to load the setup file.");
                }
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
            Init();
        }

        /// <summary>
        /// Get instrument state from VNA.
        /// Note: this uses a temporary setup file and only works when the
        /// LibreVNA-GUI runs on the same machine as METAS VNA Tools.
        /// </summary>
        /// <returns>Instrument state</returns>
        public byte[] GetState()
        {
            string path = TempSetupFile;
            try { File.Delete(path); } catch { }
            scpi.Write("DEVice:SETUP:SAVE " + path);
            // DEV:SETUP:SAVE is an event without response, wait for the file
            DateTime start = DateTime.Now;
            while (!File.Exists(path))
            {
                if ((DateTime.Now - start).TotalMilliseconds > 10000)
                {
                    throw new TimeoutException("LibreVNA-GUI did not save the setup file. " +
                        "SetState/GetState only work when the LibreVNA-GUI runs on the same machine.");
                }
                Thread.Sleep(100);
            }
            Thread.Sleep(200);
            byte[] state = File.ReadAllBytes(path);
            try { File.Delete(path); } catch { }
            return state;
        }

        /// <summary>
        /// Set instrument state to VNA.
        /// </summary>
        /// <param name="pathState">Instrument State File Path (*.is)</param>
        /// <returns>Canceled</returns>
        public bool SetState(string pathState)
        {
            return VnaHelper.SetState(this, pathState);
        }

        /// <summary>
        /// Get instrument state from VNA.
        /// </summary>
        /// <param name="pathState">Instrument State File Path (*.is)</param>
        /// <returns>Canceled</returns>
        public bool GetState(string pathState)
        {
            return VnaHelper.GetState(this, pathState);
        }

        /// <summary>
        /// Set up VNA.
        /// </summary>
        /// <param name="mode">VNA mode</param>
        public void SetUp(VnaSetUpMode mode)
        {
            SetUp(VnaHelper.GetVnaParameterMatrix(mode));
        }

        /// <summary>
        /// Set up VNA.
        /// </summary>
        /// <param name="mode">VNA mode</param>
        public void SetUp(string mode)
        {
            SetUp((VnaSetUpMode)Enum.Parse(typeof(VnaSetUpMode), mode));
        }

        private void SetUp(VnaParameter[,] p)
        {
            if (p == null) throw new Exception("VNA SetUp Mode unknown not supported.");
            int n1 = p.GetLength(0);
            int n2 = p.GetLength(1);
            int n = n1 * n2;
            if (n == 0) throw new Exception("VNA SetUp Mode unknown not supported.");
            parameters = new VnaParameter[n];
            for (int i1 = 0; i1 < n1; i1++)
            {
                for (int i2 = 0; i2 < n2; i2++)
                {
                    parameters[n2 * i1 + i2] = p[i1, i2];
                }
            }
            setupmode = VnaHelper.GetVnaSetUpMode(p);
            SetUp(parameters);
        }

        private void SetUp(VnaParameter[] parameters)
        {
            int n = parameters.Length;
            traceNames = new string[n];
            // Existing traces in the LibreVNA-GUI
            List<string> existing = new List<string>(scpi.Query("VNA:TRACe:LIST?").Trim().Split(','));
            for (int i = 0; i < existing.Count; i++) existing[i] = existing[i].Trim();
            for (int i = 0; i < n; i++)
            {
                string s = VnaParameter2String(parameters[i]);
                traceNames[i] = s;
                if (!existing.Contains(s))
                {
                    scpi.Write("VNA:TRACe:NEW " + s);
                    existing.Add(s);
                }
                scpi.Write("VNA:TRACe:PARAMeter " + s + " " + s);
            }
        }

        /// <summary>
        /// VNA Parameter to String.
        /// The LibreVNA only supports the S-parameters S11, S12, S21, S22.
        /// </summary>
        /// <param name="p">VNA Parameter</param>
        /// <returns>S-parameter string, e.g. "S21"</returns>
        internal string VnaParameter2String(VnaParameter p)
        {
            if (!p.IsSParameter || p.NumPort > 2 || p.DenPort > 2)
            {
                throw new Exception("The LibreVNA only supports the S-parameters S11, S12, S21 and S22. " +
                    "Wave and receiver parameters (switch terms, a/b waves) are not available through the SCPI interface.");
            }
            return "S" + p.NumPort.ToString() + p.DenPort.ToString();
        }

        /// <summary>
        /// Set single frequency.
        /// </summary>
        /// <param name="freq">Frequency / Hz</param>
        public void SetSingleFrequency(double freq)
        {
            FrequencyCW = freq;
            SweepMode = VnaSweepMode.CWTime;
        }

        /// <summary>
        /// Set single frequency.
        /// </summary>
        /// <param name="freq">Frequency list / Hz</param>
        /// <param name="index">Index</param>
        public void SetSingleFrequency(double[] freq, int index)
        {
            SetSingleFrequency(freq[index]);
        }

        /// <summary>
        /// Get data from VNA. All available parameters.
        /// </summary>
        /// <param name="format">VNA format</param>
        /// <returns>Data</returns>
        public VnaData<Number> GetData(VnaFormat format)
        {
            return GetData(Parameters, format);
        }

        /// <summary>
        /// Get data from VNA.
        /// </summary>
        /// <param name="parameters">VNA parameters</param>
        /// <param name="format">VNA format</param>
        /// <returns>Data</returns>
        internal VnaData<Number> GetData(VnaParameter[] parameters, VnaFormat format)
        {
            if (parameters == null || parameters.Length == 0)
            {
                throw new Exception("No VNA parameters available, call SetUp first.");
            }
            CheckCorrectionState(format);
            var ports = VnaParameter.CommonPorts(parameters);
            double z0 = Z0;
            int n2 = parameters.Length;
            VnaData<Number> data = new VnaData<Number>();
            data.Ports = ports;
            data.PortZr = new Complex<Number>[ports.Length];
            for (int i1 = 0; i1 < ports.Length; i1++)
            {
                data.PortZr[i1] = z0;
            }
            data.ParameterData = new VnaParameterData<Number>[n2];
            double[] flist = null;
            bool cw = (SweepMode == VnaSweepMode.CWTime);
            for (int i2 = 0; i2 < n2; i2++)
            {
                string trace = TraceName(parameters[i2]);
                double[] x;
                Complex<Number>[] y;
                QueryTraceData(trace, out x, out y);
                if (flist == null)
                {
                    if (cw)
                    {
                        // In zero span mode the x axis is time, use the CW frequency
                        double f = FrequencyCW;
                        flist = new double[x.Length];
                        for (int i1 = 0; i1 < x.Length; i1++) flist[i1] = f;
                    }
                    else
                    {
                        flist = x;
                    }
                    data.Frequency = flist;
                }
                else if (y.Length != flist.Length)
                {
                    throw new Exception("Trace " + trace + " has a different number of points than the other traces.");
                }
                data.ParameterData[i2] = new VnaParameterData<Number>();
                data.ParameterData[i2].Parameter = parameters[i2];
                data.ParameterData[i2].Data = y;
            }
            return data;
        }

        private string TraceName(VnaParameter p)
        {
            string s = VnaParameter2String(p);
            if (parameters != null && traceNames != null)
            {
                for (int i = 0; i < parameters.Length && i < traceNames.Length; i++)
                {
                    if (VnaParameter2String(parameters[i]) == s)
                    {
                        return traceNames[i];
                    }
                }
            }
            return s;
        }

        private void CheckCorrectionState(VnaFormat format)
        {
            // The LibreVNA-GUI applies an active calibration directly to the
            // trace data. There is no SCPI command to temporarily deactivate
            // a calibration without deleting its measurements.
            string cal = "";
            try
            {
                cal = scpi.Query("VNA:CALibration:ACTIVE?").Trim();
            }
            catch
            {
                cal = "";
            }
            bool active = !(cal.Length == 0 ||
                            cal.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
                            cal.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                            cal == "-");
            if (format == VnaFormat.RawData && active)
            {
                throw new Exception("A calibration of type '" + cal + "' is active in the LibreVNA-GUI. " +
                    "Raw data can not be read while a calibration is applied. " +
                    "Please deactivate/reset the calibration in the LibreVNA-GUI " +
                    "(METAS VNA Tools performs the error correction itself).");
            }
            if (format == VnaFormat.ErrorCorrectedData && !active)
            {
                throw new Exception("No calibration is active in the LibreVNA-GUI, " +
                    "error corrected data is not available.");
            }
        }

        private void QueryTraceData(string trace, out double[] x, out Complex<Number>[] y)
        {
            int t = scpi.Timeout;
            scpi.Timeout = 30000;
            string s = scpi.Query("VNA:TRACe:DATA? " + trace);
            scpi.Timeout = t;
            s = s.Trim();
            if (s.Length < 2)
            {
                x = new double[0];
                y = new Complex<Number>[0];
                return;
            }
            // Format: [x,real,imag],[x,real,imag],...
            s = s.TrimStart('[').TrimEnd(']');
            string[] tuples = s.Split(new string[] { "],[" }, StringSplitOptions.RemoveEmptyEntries);
            int n = tuples.Length;
            x = new double[n];
            y = new Complex<Number>[n];
            for (int i = 0; i < n; i++)
            {
                string[] v = tuples[i].Split(',');
                if (v.Length < 3)
                {
                    throw new Exception("Unexpected trace data format from LibreVNA-GUI: " + tuples[i]);
                }
                x[i] = double.Parse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture);
                double re = double.Parse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture);
                double im = double.Parse(v[2], NumberStyles.Float, CultureInfo.InvariantCulture);
                y[i] = new Complex<Number>(re, im);
            }
        }

        /// <summary>
        /// Triggers VNA, waits for sweep complete, gets and saves raw data.
        /// </summary>
        /// <param name="pathRes">Data File Path (*.sdatb, *.vdatb)</param>
        public void Measure(string pathRes)
        {
            VnaHelper.VnaMeasure(this, pathRes);
        }

        private string _rootPath = "";

        /// <summary>
        /// Root Path
        /// </summary>
        public string RootPath
        {
            get { return _rootPath; }
            set { _rootPath = value; }
        }

        /// <summary>
        /// Frequency List / Hz
        /// </summary>
        public double[] FrequencyList
        {
            get
            {
                return GetFrequencyList();
            }
        }

        private double[] GetFrequencyList()
        {
            double[] flist;
            if (SweepMode == VnaSweepMode.CWTime)
            {
                int n = SweepPoints;
                double f = FrequencyCW;
                flist = new double[n];
                for (int i = 0; i < n; i++) flist[i] = f;
                return flist;
            }
            // Try to read the stimulus values from an existing trace,
            // this works for linear and logarithmic sweeps
            if (traceNames != null && traceNames.Length > 0)
            {
                try
                {
                    double[] x;
                    Complex<Number>[] y;
                    QueryTraceData(traceNames[0], out x, out y);
                    if (x.Length == SweepPoints)
                    {
                        return x;
                    }
                }
                catch
                {
                }
            }
            // Fall back to a computed frequency list
            int points = SweepPoints;
            double start = FrequencyStart;
            double stop = FrequencyStop;
            flist = new double[points];
            if (points == 1)
            {
                flist[0] = start;
                return flist;
            }
            if (SweepMode == VnaSweepMode.LogFrequency)
            {
                double logStart = System.Math.Log10(start);
                double logStop = System.Math.Log10(stop);
                for (int i = 0; i < points; i++)
                {
                    flist[i] = System.Math.Pow(10.0, logStart + (logStop - logStart) * i / (points - 1));
                }
            }
            else
            {
                for (int i = 0; i < points; i++)
                {
                    flist[i] = start + (stop - start) * i / (points - 1);
                }
            }
            return flist;
        }

        /// <summary>
        /// Number of Test Ports
        /// </summary>
        public int NTestPorts
        {
            get
            {
                return 2;
            }
        }

        /// <summary>
        /// Settings
        /// </summary>
        public VnaSettings Settings
        {
            get { return VnaHelper.GetVnaSettings(this); }
        }

        /// <summary>
        /// Sweep Mode
        /// </summary>
        public VnaSweepMode SweepMode
        {
            get
            {
                string sweep = scpi.Query("VNA:SWEEP?").Trim();
                if (sweep.IndexOf("POWER") >= 0)
                {
                    throw new Exception("Power sweep is not supported by this driver.");
                }
                double span = scpi.QueryDouble("VNA:FREQuency:SPAN?");
                if (span == 0)
                {
                    return VnaSweepMode.CWTime;
                }
                string type = scpi.Query("VNA:SWEEPTYPE?").Trim();
                if (type.IndexOf("LOG") >= 0)
                {
                    return VnaSweepMode.LogFrequency;
                }
                return VnaSweepMode.LinearFrequency;
            }
            set
            {
                switch (value)
                {
                    case VnaSweepMode.LinearFrequency:
                        scpi.Write("VNA:SWEEP FREQUENCY");
                        scpi.Write("VNA:SWEEPTYPE LIN");
                        break;
                    case VnaSweepMode.LogFrequency:
                        scpi.Write("VNA:SWEEP FREQUENCY");
                        scpi.Write("VNA:SWEEPTYPE LOG");
                        break;
                    case VnaSweepMode.CWTime:
                        scpi.Write("VNA:SWEEP FREQUENCY");
                        scpi.Write("VNA:FREQuency:ZERO");
                        break;
                    default:
                        throw new Exception("Sweep mode " + value.ToString() + " is not supported by the LibreVNA.");
                }
            }
        }

        /// <summary>
        /// Sweep Time / s (not supported by the LibreVNA)
        /// </summary>
        public double SweepTime
        {
            get
            {
                return double.NaN;
            }
            set
            {
            }
        }

        /// <summary>
        /// Dwell Time / s
        /// </summary>
        public double DwellTime
        {
            get
            {
                return scpi.QueryDouble("VNA:ACQuisition:DWELLtime?");
            }
            set
            {
                scpi.Write("VNA:ACQuisition:DWELLtime " + value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Sweep Points
        /// </summary>
        public int SweepPoints
        {
            get
            {
                return scpi.QueryInteger("VNA:ACQuisition:POINTS?");
            }
            set
            {
                scpi.Write("VNA:ACQuisition:POINTS " + value.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// IF Bandwidth / Hz
        /// </summary>
        public double IFBandwidth
        {
            get
            {
                return scpi.QueryDouble("VNA:ACQuisition:IFBW?");
            }
            set
            {
                scpi.Write("VNA:ACQuisition:IFBW " + value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// IF Average Factor
        /// </summary>
        public int IFAverageFactor
        {
            get
            {
                int value = scpi.QueryInteger("VNA:ACQuisition:AVG?");
                if (value < 1) value = 1;
                return value;
            }
            set
            {
                if (value < 1) value = 1;
                scpi.Write("VNA:ACQuisition:AVG " + value.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// IF Average Mode
        /// </summary>
        public VnaAverageMode IFAverageMode
        {
            get
            {
                return VnaAverageMode.Sweep;
            }
            set
            {
            }
        }

        /// <summary>
        /// Start Frequency / Hz
        /// </summary>
        public double FrequencyStart
        {
            get
            {
                if (SweepMode == VnaSweepMode.CWTime)
                    return double.NaN;
                else
                    return scpi.QueryDouble("VNA:FREQuency:START?");
            }
            set
            {
                scpi.Write("VNA:FREQuency:START " + value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Stop Frequency / Hz
        /// </summary>
        public double FrequencyStop
        {
            get
            {
                if (SweepMode == VnaSweepMode.CWTime)
                    return double.NaN;
                else
                    return scpi.QueryDouble("VNA:FREQuency:STOP?");
            }
            set
            {
                scpi.Write("VNA:FREQuency:STOP " + value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Center Frequency / Hz
        /// </summary>
        public double FrequencyCenter
        {
            get
            {
                if (SweepMode == VnaSweepMode.CWTime)
                    return double.NaN;
                else
                    return scpi.QueryDouble("VNA:FREQuency:CENTer?");
            }
            set
            {
                scpi.Write("VNA:FREQuency:CENTer " + value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Span Frequency / Hz
        /// </summary>
        public double FrequencySpan
        {
            get
            {
                if (SweepMode == VnaSweepMode.CWTime)
                    return double.NaN;
                else
                    return scpi.QueryDouble("VNA:FREQuency:SPAN?");
            }
            set
            {
                scpi.Write("VNA:FREQuency:SPAN " + value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// CW Frequency / Hz
        /// </summary>
        public double FrequencyCW
        {
            get
            {
                return scpi.QueryDouble("VNA:FREQuency:CENTer?");
            }
            set
            {
                scpi.Write("VNA:FREQuency:CENTer " + value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// SegmentTable (not supported by the LibreVNA)
        /// </summary>
        public double[,] SegmentTable
        {
            get
            {
                return new double[0, 4];
            }
            set
            {
                throw new Exception("Segment sweep is not supported by the LibreVNA.");
            }
        }

        /// <summary>
        /// Source 1 Power / dBm.
        /// The LibreVNA has a single stimulus level for both ports.
        /// </summary>
        public double Source1Power
        {
            get
            {
                return scpi.QueryDouble("VNA:STIMulus:LVL?");
            }
            set
            {
                scpi.Write("VNA:STIMulus:LVL " + value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Source 1 Power Slope / (dB/GHz) (not supported by the LibreVNA)
        /// </summary>
        public double Source1PowerSlope
        {
            get
            {
                return 0;
            }
            set
            {
            }
        }

        /// <summary>
        /// Port 1 Attenuator / dB (not available on the LibreVNA)
        /// </summary>
        public double Port1Attenuator
        {
            get
            {
                return 0;
            }
            set
            {
            }
        }

        /// <summary>
        /// Port 1 Extension / s (not supported by this driver, keep at 0)
        /// </summary>
        public double Port1Extension
        {
            get
            {
                return 0;
            }
            set
            {
            }
        }

        /// <summary>
        /// Source 2 Power / dBm.
        /// The LibreVNA has a single stimulus level for both ports.
        /// </summary>
        public double Source2Power
        {
            get
            {
                return scpi.QueryDouble("VNA:STIMulus:LVL?");
            }
            set
            {
                scpi.Write("VNA:STIMulus:LVL " + value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Source 2 Power Slope / (dB/GHz) (not supported by the LibreVNA)
        /// </summary>
        public double Source2PowerSlope
        {
            get
            {
                return 0;
            }
            set
            {
            }
        }

        /// <summary>
        /// Port 2 Attenuator / dB (not available on the LibreVNA)
        /// </summary>
        public double Port2Attenuator
        {
            get
            {
                return 0;
            }
            set
            {
            }
        }

        /// <summary>
        /// Port 2 Extension / s (not supported by this driver, keep at 0)
        /// </summary>
        public double Port2Extension
        {
            get
            {
                return 0;
            }
            set
            {
            }
        }

        /// <summary>
        /// Source Port Settings.
        /// The LibreVNA has a single stimulus level for both ports.
        /// </summary>
        public VnaSourcePortSettings[] SourcePorts
        {
            get
            {
                double power = Source1Power;
                VnaSourcePortSettings[] s = new VnaSourcePortSettings[NTestPorts];
                for (int i = 0; i < NTestPorts; i++)
                {
                    s[i] = new VnaSourcePortSettings()
                    {
                        PortState = VnaSourcePortState.Auto,
                        PortPower = power,
                        PortSlope = 0,
                        PortAttenuator = 0
                    };
                }
                return s;
            }
            set
            {
                int n = value != null ? value.Length : 0;
                if (n == 0) return;
                for (int i = 1; i < n; i++)
                {
                    if (value[i].PortPower != value[0].PortPower)
                    {
                        throw new Exception("The LibreVNA has a single stimulus level, " +
                            "different source powers per port are not supported.");
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    if (value[i].PortState == VnaSourcePortState.On)
                    {
                        throw new Exception("Permanently on source ports are not supported by the LibreVNA.");
                    }
                    if (value[i].PortSlope != 0)
                    {
                        throw new Exception("Source power slope is not supported by the LibreVNA.");
                    }
                }
                Source1Power = value[0].PortPower;
            }
        }

        /// <summary>
        /// Z0 / Ohm (fixed 50 Ohm on the LibreVNA)
        /// </summary>
        public double Z0
        {
            get
            {
                return 50;
            }
            set
            {
                if (value != 50)
                {
                    throw new Exception("The reference impedance of the LibreVNA is fixed to 50 Ohm.");
                }
            }
        }

        /// <summary>
        /// Turns the acquisition (and therefore the stimulus) ON or OFF.
        /// </summary>
        public bool OutputState
        {
            get
            {
                return scpi.QueryBoolean("VNA:ACQuisition:RUN?");
            }
            set
            {
                if (value)
                {
                    scpi.Write("VNA:ACQuisition:RUN");
                }
                else
                {
                    scpi.Write("VNA:ACQuisition:STOP");
                }
            }
        }

        /// <summary>
        /// VNA parameter matrix
        /// </summary>
        public VnaParameter[,] ParameterMatrix
        {
            get
            {
                return parameterMatrix;
            }
            set
            {
                parameterMatrix = null;
                SetUp(value);
                parameterMatrix = value;
            }
        }

        /// <summary>
        /// VNA Set up mode
        /// </summary>
        public VnaSetUpMode SetUpMode
        {
            get
            {
                return setupmode;
            }
        }

        /// <summary>
        /// List with available parameters
        /// </summary>
        public VnaParameter[] Parameters
        {
            get
            {
                return parameters;
            }
        }

        #endregion
    }

    /// <summary>
    /// Simple SCPI over TCP session for the LibreVNA-GUI SCPI server.
    /// Commands and responses are newline terminated ASCII strings.
    /// </summary>
    public class LibreVnaScpiSession : IDisposable
    {
        private TcpClient tcp;
        private NetworkStream stream;
        private readonly List<byte> rxBuffer = new List<byte>();
        private readonly byte[] chunk = new byte[4096];
        private int timeout = 5000;

        /// <summary>
        /// Creates and opens a SCPI TCP session.
        /// </summary>
        /// <param name="host">Host name or IP address of the machine running the LibreVNA-GUI</param>
        /// <param name="port">TCP port of the SCPI server (default 19542)</param>
        public LibreVnaScpiSession(string host, int port)
        {
            tcp = new TcpClient();
            IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(5000))
            {
                tcp.Close();
                throw new Exception("Could not connect to the LibreVNA-GUI SCPI server at " + host + ":" +
                    port.ToString(CultureInfo.InvariantCulture) +
                    ". Make sure the LibreVNA-GUI is running and the SCPI server is enabled " +
                    "(Window -> Preferences -> General -> SCPI Control).");
            }
            tcp.EndConnect(ar);
            tcp.NoDelay = true;
            stream = tcp.GetStream();
            Timeout = timeout;
        }

        /// <summary>
        /// I/O timeout / ms
        /// </summary>
        public int Timeout
        {
            get { return timeout; }
            set
            {
                timeout = value;
                if (stream != null)
                {
                    stream.ReadTimeout = value;
                    stream.WriteTimeout = value;
                }
            }
        }

        /// <summary>
        /// Sends a SCPI command (event, no response expected).
        /// </summary>
        /// <param name="command">SCPI command</param>
        public void Write(string command)
        {
            byte[] b = Encoding.ASCII.GetBytes(command + "\n");
            stream.Write(b, 0, b.Length);
        }

        /// <summary>
        /// Reads a newline terminated response.
        /// </summary>
        /// <returns>Response without the newline character</returns>
        public string ReadLine()
        {
            DateTime start = DateTime.Now;
            while (true)
            {
                int idx = rxBuffer.IndexOf((byte)'\n');
                if (idx >= 0)
                {
                    string line = Encoding.ASCII.GetString(rxBuffer.ToArray(), 0, idx);
                    rxBuffer.RemoveRange(0, idx + 1);
                    return line.TrimEnd('\r');
                }
                if ((DateTime.Now - start).TotalMilliseconds > timeout)
                {
                    throw new TimeoutException("Timeout while reading from the LibreVNA-GUI SCPI server.");
                }
                int n = stream.Read(chunk, 0, chunk.Length);
                if (n <= 0)
                {
                    throw new Exception("The LibreVNA-GUI closed the SCPI connection. " +
                        "Note that only one client can be connected at a time.");
                }
                for (int i = 0; i < n; i++) rxBuffer.Add(chunk[i]);
            }
        }

        /// <summary>
        /// Sends a SCPI query and reads the response.
        /// </summary>
        /// <param name="command">SCPI query</param>
        /// <returns>Response</returns>
        public string Query(string command)
        {
            Write(command);
            return ReadLine();
        }

        /// <summary>
        /// Sends a SCPI query and parses the response as double.
        /// </summary>
        /// <param name="command">SCPI query</param>
        /// <returns>Response as double</returns>
        public double QueryDouble(string command)
        {
            return double.Parse(Query(command).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Sends a SCPI query and parses the response as integer.
        /// </summary>
        /// <param name="command">SCPI query</param>
        /// <returns>Response as integer</returns>
        public int QueryInteger(string command)
        {
            return (int)System.Math.Round(QueryDouble(command));
        }

        /// <summary>
        /// Sends a SCPI query and parses the response as boolean (TRUE/FALSE).
        /// </summary>
        /// <param name="command">SCPI query</param>
        /// <returns>Response as boolean</returns>
        public bool QueryBoolean(string command)
        {
            string s = Query(command).Trim();
            return s.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        /// <summary>
        /// Closes the TCP session.
        /// </summary>
        public void Dispose()
        {
            if (stream != null)
            {
                try { stream.Dispose(); } catch { }
                stream = null;
            }
            if (tcp != null)
            {
                try { tcp.Close(); } catch { }
                tcp = null;
            }
        }
    }
}
