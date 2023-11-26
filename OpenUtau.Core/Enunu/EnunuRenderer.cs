﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NumSharp;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core.Enunu {
    public class EnunuRenderer : IRenderer {

        static readonly HashSet<string> supportedExp = new HashSet<string>(){
            Format.Ustx.DYN,
            Format.Ustx.CLR,
            Format.Ustx.PITD,
            Format.Ustx.GENC,
            Format.Ustx.BREC,
            Format.Ustx.TENC,
            Format.Ustx.VOIC,
        };

        struct AcousticResult {
            public string path_acoustic;
            public string path_f0;
            public string path_spectrogram;
            public string path_aperiodicity;
        }

        struct AcousticResponse {
            public string error;
            public AcousticResult result;
        }

        struct SyntheResult {
            public string path_wav;
        }

        struct SyntheResponse {
            public string error;
            public SyntheResult result;
        }

        static readonly object lockObj = new object();

        public USingerType SingerType => USingerType.Enunu;

        public bool SupportsRenderPitch => true;

        public bool SupportsExpression(UExpressionDescriptor descriptor) {
            return supportedExp.Contains(descriptor.abbr);
        }

        public RenderResult Layout(RenderPhrase phrase) {
            var headMs = phrase.positionMs - phrase.timeAxis.TickPosToMsPos(phrase.position - EnunuUtils.headTicks);
            var tailMs = phrase.timeAxis.TickPosToMsPos(phrase.end + EnunuUtils.tailTicks) - phrase.endMs;
            return new RenderResult() {
                leadingMs = headMs,
                positionMs = phrase.positionMs,
                estimatedLengthMs = headMs + phrase.durationMs + tailMs,
            };
        }

        public Task<RenderResult> Render(RenderPhrase phrase, Progress progress, int trackNo, CancellationTokenSource cancellation, bool isPreRender) {
            var task = Task.Run(() => {
                lock (lockObj) {
                    if (cancellation.IsCancellationRequested) {
                        return new RenderResult();
                    }
                    string progressInfo = $"Track {trackNo + 1}: {this} \"{string.Join(" ", phrase.phones.Select(p => p.phoneme))}\"";
                    progress.Complete(0, progressInfo);
                    var tmpPath = Path.Join(PathManager.Inst.CachePath, $"enu-{phrase.preEffectHash:x16}");
                    var ustPath = tmpPath + ".tmp";
                    var enutmpPath = tmpPath + "_enutemp";
                    var wavPath = Path.Join(PathManager.Inst.CachePath, $"enu-{phrase.hash:x16}.wav");
                    var result = Layout(phrase);
                    if (!File.Exists(wavPath)) {
                        var config = EnunuConfig.Load(phrase.singer);
                        if (config.extensions.wav_synthesizer.Contains("synthe")) {
                            Log.Information($"Starting enunu synthesis \"{ustPath}\"");
                            var enunuNotes = PhraseToEnunuNotes(phrase);
                            // TODO: using first note tempo as ust tempo.
                            EnunuUtils.WriteUst(enunuNotes, phrase.phones.First().tempo, phrase.singer, ustPath);
                            var response = EnunuClient.Inst.SendRequest<SyntheResponse>(new string[] { "synthe", ustPath, wavPath });
                            if (response.error != null) {
                                throw new Exception(response.error);
                            }
                        } else {
                        var f0Path = Path.Join(enutmpPath, "f0.npy");
                        var spPath = Path.Join(enutmpPath, "spectrogram.npy");
                        var apPath = Path.Join(enutmpPath, "aperiodicity.npy");
                        if (!File.Exists(f0Path) || !File.Exists(spPath) || !File.Exists(apPath)) {
                            Log.Information($"Starting enunu acoustic \"{ustPath}\"");
                            var enunuNotes = PhraseToEnunuNotes(phrase);
                            // TODO: using first note tempo as ust tempo.
                            EnunuUtils.WriteUst(enunuNotes, phrase.phones.First().tempo, phrase.singer, ustPath);
                            var response = EnunuClient.Inst.SendRequest<AcousticResponse>(new string[] { "acoustic", ustPath });
                            if (response.error != null) {
                                throw new Exception(response.error);
                            }
                        }
                        if (cancellation.IsCancellationRequested) {
                            return new RenderResult();
                        }
                        var f0 = np.Load<double[]>(f0Path);
                        var sp = np.Load<double[,]>(spPath);
                        var ap = np.Load<double[,]>(apPath);
                        int totalFrames = f0.Length;
                        var headMs = phrase.positionMs - phrase.timeAxis.TickPosToMsPos(phrase.position - EnunuUtils.headTicks);
                        var tailMs = phrase.timeAxis.TickPosToMsPos(phrase.end + EnunuUtils.tailTicks) - phrase.endMs;
                        int headFrames = (int)Math.Round(headMs / config.framePeriod);
                        int tailFrames = (int)Math.Round(tailMs / config.framePeriod);
                        var editorF0 = SampleCurve(phrase, phrase.pitches, 0, config.framePeriod, totalFrames, headFrames, tailFrames, x => MusicMath.ToneToFreq(x * 0.01));
                        var gender = SampleCurve(phrase, phrase.gender, 0.5, config.framePeriod, totalFrames, headFrames, tailFrames, x => 0.5 + 0.005 * x);
                        var tension = SampleCurve(phrase, phrase.tension, 0.5, config.framePeriod, totalFrames, headFrames, tailFrames, x => 0.5 + 0.005 * x);
                        var breathiness = SampleCurve(phrase, phrase.breathiness, 0.5, config.framePeriod, totalFrames, headFrames, tailFrames, x => 0.5 + 0.005 * x);
                        var voicing = SampleCurve(phrase, phrase.voicing, 1.0, config.framePeriod, totalFrames, headFrames, tailFrames, x => 0.01 * x);
                        int fftSize = (sp.GetLength(1) - 1) * 2;
                        for (int i = 0; i < f0.Length; i++) {
                            if (f0[i] < 50) {
                                editorF0[i] = 0;
                            }
                        }
                        var samples = Worldline.WorldSynthesis(
                            editorF0,
                            sp, false, sp.GetLength(1),
                            ap, false, fftSize,
                            config.framePeriod, config.sampleRate,
                            gender, tension, breathiness, voicing);
                        result.samples = samples.Select(d => (float)d).ToArray();
                        Wave.CorrectSampleScale(result.samples);
                        if (config.sampleRate != 44100) {
                            var signal = new NWaves.Signals.DiscreteSignal(config.sampleRate, result.samples);
                            signal = NWaves.Operations.Operation.Resample(signal, 44100);
                            result.samples = signal.Samples;
                        }
                        var source = new WaveSource(0, 0, 0, 1);
                        source.SetSamples(result.samples);
                        WaveFileWriter.CreateWaveFile16(wavPath, new ExportAdapter(source).ToMono(1, 0));
                    }
                    }
                    progress.Complete(phrase.phones.Length, progressInfo);
                    if (File.Exists(wavPath)) {
                        using (var waveStream = Wave.OpenFile(wavPath)) {
                            result.samples = Wave.GetSamples(waveStream.ToSampleProvider().ToMono(1, 0));
                        }
                        if (result.samples != null) {
                            Renderers.ApplyDynamics(phrase, result);
                        }
                    } else {
                        result.samples = new float[0];
                    }
                    return result;
                }
            });
            return task;
        }

        double[] SampleCurve(RenderPhrase phrase, float[] curve, double defaultValue, double frameMs, int length, int headFrames, int tailFrames, Func<double, double> convert) {
            const int interval = 5;
            var result = new double[length];
            if (curve == null) {
                Array.Fill(result, defaultValue);
                return result;
            }
            for (int i = 0; i < length - headFrames - tailFrames; i++) {
                double posMs = phrase.positionMs - phrase.leadingMs + i * frameMs;
                int ticks = phrase.timeAxis.MsPosToTickPos(posMs) - (phrase.position - phrase.leading);
                int index = Math.Max(0, (int)((double)ticks / interval));
                if (index < curve.Length) {
                    result[i + headFrames] = convert(curve[index]);
                }
            }
            Array.Fill(result, defaultValue, 0, headFrames);
            Array.Fill(result, defaultValue, length - tailFrames, tailFrames);
            return result;
        }

        public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) {
            var tmpPath = Path.Join(PathManager.Inst.CachePath, $"enu-{phrase.preEffectHash:x16}");
            var enutmpPath = tmpPath + "_enutemp";
            var f0Path = Path.Join(enutmpPath, "f0.npy");
            if (!File.Exists(f0Path)) {
                return null;
            }
            var config = EnunuConfig.Load(phrase.singer);
            var f0 = np.Load<double[]>(f0Path);
            var result = new RenderPitchResult() {
                tones = f0.Select(f => (float)MusicMath.FreqToTone(f)).ToArray(),
            };
            result.ticks = new float[result.tones.Length];
            var layout = Layout(phrase);
            var t = layout.positionMs - layout.leadingMs;
            for (int i = 0; i < result.tones.Length; i++) {
                t += config.framePeriod;
                result.ticks[i] = phrase.timeAxis.MsPosToTickPos(t) - phrase.position;
            }
            return result;
        }

        static EnunuNote[] PhraseToEnunuNotes(RenderPhrase phrase) {
            var notes = new List<EnunuNote>();
            notes.Add(new EnunuNote {
                lyric = "R",
                length = EnunuUtils.headTicks,
                noteNum = 60,
            });
            foreach (var phone in phrase.phones) {
                notes.Add(new EnunuNote {
                    lyric = phone.phoneme,
                    length = phone.duration,
                    noteNum = phone.tone,
                    timbre = phone.suffix,
                });
            }
            notes.Add(new EnunuNote {
                lyric = "R",
                length = EnunuUtils.tailTicks,
                noteNum = 60,
            });
            return notes.ToArray();
        }

        public UExpressionDescriptor[] GetSuggestedExpressions(USinger singer, URenderSettings renderSettings) {
            return new UExpressionDescriptor[] { };
        }

        public override string ToString() => Renderers.ENUNU;
    }
}
