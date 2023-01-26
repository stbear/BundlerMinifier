using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NUglify;

namespace BundlerMinifier
{
    public static class BundleMinifier
    {
        private static readonly string _path = Path.Combine(Path.GetTempPath(), "BundlerMinifier" + Constants.VERSION);
        public static MinificationResult MinifyBundle(Bundle bundle)
        {
            string file = bundle.GetAbsoluteOutputFile();
            string extension = Path.GetExtension(file).ToUpperInvariant();
            var minResult = new MinificationResult(file, null, null);
            if (!string.IsNullOrEmpty(bundle.Output) && bundle.IsMinificationEnabled)
            {
                try
                {
                    switch (extension)
                    {
                        case ".JS":
                            MinifyJavaScript(bundle, minResult);
                            break;
                        case ".CSS":
                            MinifyCss(bundle, minResult);
                            break;
                        case ".HTML":
                        case ".HTM":
                            MinifyHtml(bundle, minResult);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    AddGenericException(minResult, ex);
                }
            }

            if (minResult.HasErrors)
            {
                OnErrorMinifyingFile(minResult);
            }

            return minResult;
        }

        private static void SaveResourceFile(string path, string resourceName, string fileName)
        {
            using (Stream stream = typeof(BundleMinifier).Assembly.GetManifestResourceStream(resourceName))
            using (FileStream fs = new FileStream(Path.Combine(path, fileName), FileMode.Create))
            {
                stream.CopyTo(fs);
            }
        }

        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        private static void MinifyJavaScript(Bundle bundle, MinificationResult minResult)
        {
            var settings = JavaScriptOptions.GetSettings(bundle);

            var node_modules = Path.Combine(_path, "node_modules");
            var log_file = Path.Combine(_path, "log.txt");
            if (!Directory.Exists(node_modules) || !File.Exists(log_file))
            {
                if (Directory.Exists(_path))
                    Directory.Delete(_path, true);

                Directory.CreateDirectory(_path);
                SaveResourceFile(_path, "BundlerMinifier.Node.node.zip", "node.zip");

                System.IO.Compression.ZipFile.ExtractToDirectory(Path.Combine(_path, "node.zip"), _path);

                // If this file is written, then the initialization was successful.
                File.WriteAllText(log_file, DateTime.Now.ToLongDateString());
            }

            string slash = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "\\" : "/";
            string arguments = ConstructArguments(bundle);
            File.WriteAllText(log_file, new FileInfo(bundle.FileName).DirectoryName + " " + $"\"{Path.Combine(_path, $"node_modules{slash}uglify-js{slash}bin{slash}uglifyjs")}\" {arguments}");
            ProcessStartInfo start = new ProcessStartInfo
            {
                WorkingDirectory = Path.GetPathRoot(bundle.FileName),
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                FileName = "node",
                Arguments = $"\"{Path.Combine(_path, $"node_modules{slash}uglify-js{slash}bin{slash}uglifyjs")}\" {arguments}",
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            start.EnvironmentVariables["PATH"] = _path + ";" + start.EnvironmentVariables["PATH"];
            start.EnvironmentVariables["NODE_SKIP_PLATFORM_CHECK"] = "1";

            using (Process p = Process.Start(start))
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                p.WaitForExit();

                var error = stderr.Result.Trim();
                if (!string.IsNullOrEmpty(error))
                {
                    var result = new MinificationResult(bundle.FileName, string.Empty, string.Empty);
                    result.Errors.Add(new MinificationError { Message = error });
                    OnErrorMinifyingFile(result);
                }
                else
                {
                    OnAfterWritingMinFile(bundle.FileName, bundle.OutputFileName, bundle, true);
                }
            }
        }

        private static string ConstructArguments(Bundle bundle)
        {
            var result = string.Join(" ", bundle.GetAbsoluteInputFiles().Select(x => $"\"{x}\""));
            if (bundle.IsMinificationEnabled)
                result += " --mangle";
            result += $" --output \"{bundle.GetAbsoluteOutputFile()}\"";
            if (bundle.SourceMap)
                result += $" --source-map";
            return result;
        }

        private static void MinifyCss(Bundle bundle, MinificationResult minResult)
        {
            var settings = CssOptions.GetSettings(bundle);

            var uglifyResult = Uglify.Css(bundle.Output, minResult.FileName, settings);
            WriteMinFile(bundle, minResult, uglifyResult);
        }

        private static void MinifyHtml(Bundle bundle, MinificationResult minResult)
        {
            var settings = HtmlOptions.GetSettings(bundle);

            var uglifyResult = Uglify.Html(bundle.Output, settings, minResult.FileName);
            WriteMinFile(bundle, minResult, uglifyResult);
        }

        private static void WriteMinFile(Bundle bundle, MinificationResult minResult, UglifyResult uglifyResult)
        {
            var minFile = GetMinFileName(minResult.FileName);
            minResult.MinifiedContent = uglifyResult.Code?.Trim();

            if (!uglifyResult.HasErrors)
            {
                bool containsChanges = FileHelpers.HasFileContentChanged(minFile, minResult.MinifiedContent);
                minResult.Changed |= containsChanges;
                OnBeforeWritingMinFile(minResult.FileName, minFile, bundle, containsChanges);

                if (containsChanges)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(minFile));
                    File.WriteAllText(minFile, minResult.MinifiedContent, new UTF8Encoding(false));
                    OnAfterWritingMinFile(minResult.FileName, minFile, bundle, containsChanges);
                }
            }
            else
            {
                AddNUglifyErrors(uglifyResult, minResult);
            }
        }

        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        public static void GzipFile(string sourceFile, Bundle bundle, bool minificationChanged, string minifiedContent)
        {
            var gzipFile = sourceFile + ".gz";
            var containsChanges = minificationChanged || File.GetLastWriteTimeUtc(gzipFile) < File.GetLastWriteTimeUtc(sourceFile);

            OnBeforeWritingGzipFile(sourceFile, gzipFile, bundle, containsChanges);

            if (containsChanges)
            {
                byte[] buffer = Encoding.UTF8.GetBytes(minifiedContent ?? bundle.Output);

                using (var fileStream = File.OpenWrite(gzipFile))
                using (var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal))
                {
                    gzipStream.Write(buffer, 0, buffer.Length);
                }

                OnAfterWritingGzipFile(sourceFile, gzipFile, bundle, containsChanges);
            }
        }

        private static void AddNUglifyErrors(UglifyResult minifier, MinificationResult minResult)
        {
            foreach (var error in minifier.Errors)
            {
                var minError = new MinificationError
                {
                    FileName = minResult.FileName,
                    Message = error.Message,
                    LineNumber = error.StartLine,
                    ColumnNumber = error.StartColumn
                };

                minResult.Errors.Add(minError);
            }
        }

        private static void AddGenericException(MinificationResult minResult, Exception ex)
        {
            minResult.Errors.Add(new MinificationError
            {
                FileName = minResult.FileName,
                Message = ex.Message,
                LineNumber = 0,
                ColumnNumber = 0
            });
        }

        public static string GetMinFileName(string file)
        {
            string fileName = Path.GetFileName(file);

            if (fileName.IndexOf(".min.", StringComparison.OrdinalIgnoreCase) > 0)
                return file;

            string ext = Path.GetExtension(file);
            return file.Substring(0, file.LastIndexOf(ext, StringComparison.OrdinalIgnoreCase)) + ".min" + ext;
        }

        static void OnBeforeWritingMinFile(string file, string minFile, Bundle bundle, bool containsChanges)
        {
            BeforeWritingMinFile?.Invoke(null, new MinifyFileEventArgs(file, minFile, bundle, containsChanges));
        }

        static void OnAfterWritingMinFile(string file, string minFile, Bundle bundle, bool containsChanges)
        {
            AfterWritingMinFile?.Invoke(null, new MinifyFileEventArgs(file, minFile, bundle, containsChanges));
        }

        static void OnBeforeWritingGzipFile(string minFile, string gzipFile, Bundle bundle, bool containsChanges)
        {
            BeforeWritingGzipFile?.Invoke(null, new MinifyFileEventArgs(minFile, gzipFile, bundle, containsChanges));
        }

        static void OnAfterWritingGzipFile(string minFile, string gzipFile, Bundle bundle, bool containsChanges)
        {
            AfterWritingGzipFile?.Invoke(null, new MinifyFileEventArgs(minFile, gzipFile, bundle, containsChanges));
        }

        static void OnErrorMinifyingFile(MinificationResult result)
        {
            if (ErrorMinifyingFile != null)
            {
                var e = new MinifyFileEventArgs(result.FileName, null, null, false);
                e.Result = result;

                ErrorMinifyingFile(null, e);
            }
        }

        public static event EventHandler<MinifyFileEventArgs> BeforeWritingMinFile;
        public static event EventHandler<MinifyFileEventArgs> AfterWritingMinFile;
        public static event EventHandler<MinifyFileEventArgs> BeforeWritingGzipFile;
        public static event EventHandler<MinifyFileEventArgs> AfterWritingGzipFile;
        public static event EventHandler<MinifyFileEventArgs> ErrorMinifyingFile;
    }
}
