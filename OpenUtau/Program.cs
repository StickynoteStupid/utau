﻿using System;
using System.Text;
using System.Threading.Tasks;
using AutoUpdaterDotNET;

namespace OpenUtau {
    class Program {

        [STAThread]
        private static void Main(string[] args) {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            App.Program.InitLogging();
            App.Program.InitOpenUtau(args);
            InitAudio();

            if (Core.Util.Preferences.Default.Beta == 0 && !Core.DocManager.Inst.isVst) {
                App.Program.InitInterop();
                new WpfApp().Run(new UI.MainWindow());
            } else {
                App.Program.AutoUpdate = () => AutoUpdater.Start("https://github.com/stakira/OpenUtau/releases/download/OpenUtau-Latest/release.xml");
                App.Program.Run(args);
            }
        }

        private static void InitAudio() {
            Core.PlaybackManager.Inst.AudioOutput = new Audio.WaveOutAudioOutput();
            Core.Formats.Wave.OverrideMp3Reader = filepath => new NAudio.Wave.AudioFileReader(filepath);
        }
    }
}
