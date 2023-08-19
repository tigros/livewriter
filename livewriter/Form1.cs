using Microsoft.Win32;
using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace livewriter
{
    public partial class Form1 : Form
    {
        WaveIn sourceStream;
        WasapiLoopbackCapture sysin;
        WaveFileWriter waveWriter;
        WaveFileWriter sysWriter;
        ConcurrentQueue<string> whisperq = new ConcurrentQueue<string>();
        bool quitq = false;
        Dictionary<string, string> langs = new Dictionary<string, string>();
        string glbmodel = "";
        string currenttmp, currentsystmp;
        int totavg = 0;
        int totavgsys = 0;

        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            try
            {
                if (File.Exists("languageCodez.tsv"))
                {
                    foreach (string line in File.ReadLines("languageCodez.tsv"))
                    {
                        string[] lang = line.Split('\t');
                        string proper = toproper(lang[2]);
                        langs.Add(proper, lang[0]); comboBox1.Items.Add(proper);
                    }
                }
                else
                {
                    MessageBox.Show("languageCodez.tsv missing!");
                    langs.Add("English", "en");
                    comboBox1.Items.Add("English");
                }
                textBox1.Text = readreg("modelpath", textBox1.Text);
                comboBox1.Text = readreg("language", "English");
                numericUpDown1.Value = Convert.ToDecimal(readreg("frequency", "10"));
                setinputs();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }

        string toproper(string s)
        {
            return s.Substring(0, 1).ToUpper() + s.Substring(1);
        }

        void setinputs()
        {
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                comboBox2.Items.Add(WaveIn.GetCapabilities(i).ProductName);
            comboBox2.Items.Add("System sound only");
            comboBox2.SelectedIndex = 0;
        }

        void writereg(string name, string value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\tigros\livewriter"))
                {
                    if (key != null)
                        key.SetValue(name, value);
                }
            }
            catch { }
        }

        string readreg(string name, string deflt)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\tigros\livewriter"))
                {
                    if (key != null)
                    {
                        object o = key.GetValue(name);
                        if (o != null)
                            return (string)o;
                    }
                }
            }
            catch { }
            return deflt;
        }

        int getdevicenumber()
        {
            int dev = comboBox2.SelectedIndex;
            if (dev == comboBox2.Items.Count - 1)
                dev = -1;
            return dev;
        }

        void initandstart()
        {
            int dev = getdevicenumber();
            if (sourceStream == null && dev != -1)
            {
                sourceStream = new WaveIn
                {
                    DeviceNumber = dev,
                    WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(32000, 1)
                };
                sourceStream.DataAvailable += SourceStream_DataAvailable;
                currenttmp = newtmp();
                waveWriter = new WaveFileWriter(currenttmp, sourceStream.WaveFormat);
                sourceStream.StartRecording();
            }

            if ((checkBox1.Checked || dev == -1) && sysin == null)
            {
                sysin = new WasapiLoopbackCapture();
                sysin.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(32000, 1);
                sysin.DataAvailable += Sysin_DataAvailable;
                currentsystmp = newtmp();
                sysWriter = new WaveFileWriter(currentsystmp, sysin.WaveFormat);
                sysin.StartRecording();
            }
        }

        int getavg(WaveInEventArgs e)
        {
            int tot = 0, cnt = 0;
            for (int i = 0; i < e.BytesRecorded; i++)
            {
                byte v = e.Buffer[i];
                if (v < 255)
                {
                    tot += v;
                    cnt++;
                }
            }
            return cnt == 0 ? 0 : tot / cnt;
        }

        string newtmp()
        {
            string tmp = Path.GetTempFileName();
            File.Delete(tmp);
            tmp += ".wav";
            return tmp;
        }

        private void SourceStream_DataAvailable(object sender, WaveInEventArgs e)
        {
            waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
            waveWriter.Flush();
            totavg += getavg(e);
        }

        private void Sysin_DataAvailable(object sender, WaveInEventArgs e)
        {
            sysWriter.Write(e.Buffer, 0, e.BytesRecorded);
            sysWriter.Flush();
            totavgsys += getavg(e);
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            if (totavg > numericUpDown1.Value * 100)
            {
                stopmic();
                stopwavewriter();
                whisperq.Enqueue(currenttmp);
                initandstart();
            }

            if (totavgsys > numericUpDown1.Value * 100)
            {
                stopsys();
                stopsyswriter();
                whisperq.Enqueue(currentsystmp);
                initandstart();
            }

            totavg = totavgsys = 0;
        }

        void stopmic()
        {
            if (sourceStream != null)
            {
                sourceStream.DataAvailable -= SourceStream_DataAvailable;
                try
                {
                    sourceStream.StopRecording();
                }
                catch { }
                sourceStream.Dispose();
                sourceStream = null;
            }
        }

        void stopsys()
        {
            if (sysin != null)
            {
                sysin.DataAvailable -= Sysin_DataAvailable;
                try
                {
                    sysin.StopRecording();
                }
                catch { }
                sysin.Dispose();
                sysin = null;
            }
        }

        void stopwavewriter()
        {
            if (waveWriter != null)
            {
                waveWriter.Dispose();
                waveWriter = null;
            }
        }

        void stopsyswriter()
        {
            if (sysWriter != null)
            {
                sysWriter.Dispose();
                sysWriter = null;
            }
        }

        void stop()
        {
            timer1.Enabled = false;
            quitq = true;
            stopmic();
            stopsys();
            stopwavewriter();
            stopsyswriter();
        }

        void whisper(string filename)
        {
            try
            {
                string lang = "en";
                Invoke(new Action(() =>
                {
                    lang = langs[comboBox1.Text];
                }));
                Process proc = new Process();
                string translate = " ";
                if (checkBox2.Checked)
                    translate = " -tr ";
                proc.StartInfo.FileName = "main.exe";
                proc.StartInfo.Arguments = "--language " + lang + translate + "--output-txt --no-timestamps --max-context 0 --model \"" +
                    glbmodel + "\" \"" + filename + "\"";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.EnableRaisingEvents = true;
                proc.Exited += whisper_Exited;
                proc.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }

        string getfilename(Process p)
        {
            string filename = p.StartInfo.Arguments;
            filename = filename.TrimEnd('"');
            return filename.Substring(filename.LastIndexOf('"') + 1);
        }

        string nonewline(string s)
        {
            return s.Replace(Environment.NewLine, "\r");
        }

        private void whisper_Exited(object sender, EventArgs e)
        {
            try
            {
                string filename = getfilename((Process)sender);
                if (File.Exists(filename))
                    File.Delete(filename);
                string txt = filename.Remove(filename.Length - 4) + ".txt";
                if (File.Exists(txt))
                {
                    string s = File.ReadAllText(txt).Trim();
                    if (s != "" && timer1.Enabled)
                    {
                        Debug.WriteLine(s);
                        SendKeys.SendWait(nonewline(s + Environment.NewLine));
                    }
                    File.Delete(txt);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }           
        }

        void consumeq()
        {
            string file = "";
            while (!quitq)
            {
                while (!quitq && whisperq.TryDequeue(out file))
                    whisper(file);
                Thread.Sleep(500);
            }
        }

        void startq()
        {
            string f = "";
            while (whisperq.Count > 0)
            {
                while (whisperq.TryDequeue(out f))
                    ;
                Thread.Sleep(10);
            }
            quitq = false;

            Thread cq = new Thread(() =>
            {
                consumeq();
            });
            cq.IsBackground = true;
            cq.Start();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            try
            {
                if (button1.Text == "Go Live!")
                {
                    glbmodel = textBox1.Text;
                    if (!File.Exists(glbmodel))
                    {
                        MessageBox.Show(glbmodel + " not found!");
                        return;
                    }
                    initandstart();
                    button1.Text = "Stop";
                    startq();
                    timer1.Interval = (int)numericUpDown1.Value * 1000;
                    timer1.Enabled = true;
                }
                else
                {
                    button1.Text = "Go Live!";
                    stop();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }

        private void numericUpDown1_ValueChanged(object sender, EventArgs e)
        {
            if (timer1.Enabled)
            {
                timer1.Enabled = false;
                timer1.Interval = (int)numericUpDown1.Value * 1000;
                timer1.Enabled = true;
            }
        }

        void deletetmps(string pattern)
        {
            DirectoryInfo di = new DirectoryInfo(Path.GetTempPath());
            FileInfo[] fi = di.GetFiles(pattern);
            foreach (FileInfo f in fi)
            {
                try
                {
                    f.Delete();
                }
                catch { }
            }
        }

        void waitanddelete()
        {
            Thread thr = new Thread(() =>
            {
                while (Process.GetProcessesByName("main").Length > 0)
                    Thread.Sleep(1000);
                deletetmps("*.tmp.wav");
                deletetmps("*.tmp.txt");
            });
            thr.Start();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            stop();
            writereg("modelpath", textBox1.Text);
            writereg("language", comboBox1.Text);
            writereg("frequency", numericUpDown1.Value.ToString());
            waitanddelete();
        }
    }
}
