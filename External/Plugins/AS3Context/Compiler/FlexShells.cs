using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using ASCompletion.Context;
using ASCompletion.Model;
using PluginCore;
using PluginCore.Helpers;
using PluginCore.Localization;
using PluginCore.Managers;
using PluginCore.Utilities;

namespace AS3Context.Compiler
{
    public delegate void SyntaxErrorHandler(string error);

    /// <summary>
    /// Wrappers for Flex SDK integration
    /// </summary>
    public class FlexShells
    {
        public static event SyntaxErrorHandler SyntaxError;

        public static readonly Regex re_SplitParams = 
            new Regex("[\\s](?<switch>[-+][A-z0-9\\-\\.]+)", RegexOptions.Compiled | RegexOptions.Singleline);

        static readonly string[] PATH_SWITCHES = { 
            "-compiler.context-root","-context-root",
            "-compiler.defaults-css-url","-defaults-css-url",
            "-compiler.external-library-path","-external-library-path","-el",
            "-compiler.fonts.system-search-path","-system-search-path",
            "-compiler.include-libraries","-include-libraries",
            "-compiler.library-path","-library-path","-l",
            "-compiler.source-path","-source-path","-sp",
            "-compiler.services","-services",
            "-compiler.theme","-theme",
            "-dump-config","-file-specs","resource-bundle-list",
            "-link-report","-load-config","-load-externs","-size-report",
            "-output","-o","-runtime-shared-libraries","-rsl",
            "-namespace","-compiler.namespaces.namespace"};

        static string ascPath;
        static string mxmlcPath;
        static readonly string flexShellsJar = "Flex4Shells.jar";
        static string flexShellsPath;
        static bool running;
        static bool silentChecking;
        static string checkedSDK;
        static bool isFlex4SDK;
        static string currentSDK;

        static string CheckResource(string resName, string fileName)
        {
            var fullPath = Path.Combine(PathHelper.DataDir, "AS3Context", fileName);
            if (File.Exists(fullPath)) return fullPath;
            var id = "AS3Context.Resources." + resName;
            var assembly = Assembly.GetExecutingAssembly();
            using var br = new BinaryReader(assembly.GetManifestResourceStream(id));
            using var bw = File.Create(fullPath);
            var buffer = br.ReadBytes(1024);
            while (buffer.Length > 0)
            {
                bw.Write(buffer, 0, buffer.Length);
                buffer = br.ReadBytes(1024);
            }
            bw.Close();
            br.Close();
            return fullPath;
        }

        public static FlexShells Instance 
        {
            get { return instance ??= new FlexShells(); }
        }

        static FlexShells instance;

        FlexShells()
        {
        }

        ProcessRunner ascRunner;
        ProcessRunner mxmlcRunner;
        string builtSWF;
        bool debugMode;
        Dictionary<string, string> jvmConfig;

        public void CheckAS3(string filename, string flexPath)
        {
            CheckAS3(filename, flexPath, null);
        }

        public void CheckAS3(string filename, string flexPath, string src)
        {
            if (running) return;

            // let other plugins preprocess source/handle checking
            var data = new Hashtable {["filename"] = filename, ["src"] = src, ["ext"] = Path.GetExtension(filename)};
            var de = new DataEvent(EventType.Command, "AS3Context.CheckSyntax", data);
            EventManager.DispatchEvent(this, de);
            if (de.Handled) return;

            src = (string)data["src"];
            filename = (string)data["filename"];
            if (".mxml" == (string)data["ext"]) // MXML not supported by ASC without preprocessing
                return;

            string basePath = null;
            if (PluginBase.CurrentProject != null)
                basePath = Path.GetDirectoryName(PluginBase.CurrentProject.ProjectPath);
            flexPath = PathHelper.ResolvePath(flexPath, basePath);
            // asc.jar in FlexSDK
            if (flexPath != null && Directory.Exists(Path.Combine(flexPath, "lib")))
                ascPath = Path.Combine(flexPath, "lib\\asc.jar");
            // included asc.jar
            if (ascPath is null || !File.Exists(ascPath)) 
                ascPath = PathHelper.ResolvePath(Path.Combine(PathHelper.ToolDir, "flexlibs/lib/asc.jar"));

            if (ascPath is null)
            {
                if (src != null) return; // silent checking
                var result = MessageBox.Show(TextHelper.GetString("Info.SetFlex2OrCS3Path"), TextHelper.GetString("Title.ConfigurationRequired"), MessageBoxButtons.YesNoCancel);
                if (result == DialogResult.Yes)
                {
                    var context = ASContext.GetLanguageContext("as3");
                    if (context is null) return;
                    PluginBase.MainForm.ShowSettingsDialog("AS3Context", "SDK");
                }
                else if (result == DialogResult.No)
                {
                    PluginBase.MainForm.ShowSettingsDialog("ASCompletion", "Flash");
                }
                return;
            }

            flexShellsPath = CheckResource("FlexShells.jar", flexShellsJar);
            if (!File.Exists(flexShellsPath))
            {
                if (src != null) return; // silent checking
                ErrorManager.ShowInfo(TextHelper.GetString("Info.ResourceError"));
                return;
            }

            jvmConfig = JvmConfigHelper.ReadConfig(flexPath);
            
            try
            {
                running = true;
                if (src is null) EventManager.DispatchEvent(this, new NotifyEvent(EventType.ProcessStart));
                if (ascRunner is null || !ascRunner.IsRunning || currentSDK != flexPath)
                    StartAscRunner(flexPath);

                notificationSent = false;
                if (src is null)
                {
                    silentChecking = false;
                    //TraceManager.Add("Checking: " + filename, -1);
                    ASContext.SetStatusText(TextHelper.GetString("Info.AscRunning"));
                    ascRunner.HostedProcess.StandardInput.WriteLine(filename);
                }
                else
                {
                    silentChecking = true;
                    ascRunner.HostedProcess.StandardInput.WriteLine(filename + "$raw$");
                    ascRunner.HostedProcess.StandardInput.WriteLine(src);
                    ascRunner.HostedProcess.StandardInput.WriteLine(filename + "$raw$");
                }
            }
            catch(Exception ex)
            {
                ErrorManager.AddToLog(TextHelper.GetString("Info.CheckError"), ex);
                TraceManager.AddAsync(TextHelper.GetString("Info.CheckError") + "\n" + ex.Message);
            }
        }

        public void RunMxmlc(string cmd, string flexPath)
        {
            if (running) return;
            string basePath = null;
            if (PluginBase.CurrentProject != null)
                basePath = Path.GetDirectoryName(PluginBase.CurrentProject.ProjectPath);
            flexPath = PathHelper.ResolvePath(flexPath, basePath);

            if (flexPath != null && Directory.Exists(flexPath))
            {
                mxmlcPath = Path.Combine(flexPath, "lib", "mxmlc.jar");
            }
            if (mxmlcPath is null || !File.Exists(mxmlcPath)) 
            {
                var result = MessageBox.Show(TextHelper.GetString("Info.OpenCompilerSettings"), TextHelper.GetString("Title.ConfigurationRequired"), MessageBoxButtons.OKCancel);
                if (result == DialogResult.OK)
                {
                    var context = ASContext.GetLanguageContext("as3");
                    if (context is null) return;
                    PluginBase.MainForm.ShowSettingsDialog("AS3Context", "SDK");
                }
                return;
            }

            flexShellsPath = CheckResource("FlexShells.jar", flexShellsJar);
            if (!File.Exists(flexShellsPath))
            {
                ErrorManager.ShowInfo(TextHelper.GetString("Info.ResourceError"));
                return;
            }

            jvmConfig = JvmConfigHelper.ReadConfig(flexPath);
            
            try
            {
                running = true;
                EventManager.DispatchEvent(this, new NotifyEvent(EventType.ProcessStart));

                if (mxmlcRunner is null || !mxmlcRunner.IsRunning || currentSDK != flexPath) 
                    StartMxmlcRunner(flexPath);
                
                //cmd = mainForm.ProcessArgString(cmd);
                //TraceManager.Add("MxmlcShell command: "+cmd, -1);

                ASContext.SetStatusText(TextHelper.GetString("Info.MxmlcRunning"));
                notificationSent = false;
                mxmlcRunner.HostedProcess.StandardInput.WriteLine(cmd);
            }
            catch(Exception ex)
            {
                ErrorManager.ShowError(ex);
            }
        }

        public void QuickBuild(FileModel theFile, string flex2Path, bool requireTag, bool playAfterBuild)
        {
            if (running) return;
            // environment
            string filename = theFile.FileName;
            string currentPath = Environment.CurrentDirectory;
            string buildPath = PluginBase.MainForm.ProcessArgString("$(ProjectDir)");
            if (!Directory.Exists(buildPath) 
                || !filename.StartsWith(buildPath, StringComparison.OrdinalIgnoreCase)) 
            {
                buildPath = theFile.BasePath;
                if (!Directory.Exists(buildPath))
                    buildPath = Path.GetDirectoryName(filename);
            }
            // command
            debugMode = false;
            var hasOutput = false;
            var cmd = "";
            var mCmd = Regex.Match(PluginBase.MainForm.CurrentDocument.SciControl.Text, "\\s@mxmlc\\s(?<cmd>.*)");
            if (mCmd.Success)
            {
                try
                {
                    // cleanup tag
                    var tag = mCmd.Groups["cmd"].Value;
                    if (tag.IndexOfOrdinal("-->") > 0) tag = tag.Substring(0, tag.IndexOfOrdinal("-->"));
                    if (tag.IndexOfOrdinal("]]>") > 0) tag = tag.Substring(0, tag.IndexOfOrdinal("]]>"));
                    tag = " " + tag.Trim() + " --";

                    // split
                    var mPar = re_SplitParams.Matches(tag);
                    if (mPar.Count > 0)
                    {
                        cmd = "";
                        for (int i = 0; i < mPar.Count; i++)
                        {
                            var op = mPar[i].Groups["switch"].Value;
                            if (op == "--") break;
                            if (op == "-noplay")
                            {
                                playAfterBuild = false;
                                continue;
                            }
                            if (op == "-debug") debugMode = true;

                            int start = mPar[i].Index + mPar[i].Length;
                            int end = (i < mPar.Count - 1) ? mPar[i + 1].Index : 0;
                            if (end > start)
                            {
                                string concat = ";";
                                var arg = tag.Substring(start, end - start).Trim();
                                if (arg.StartsWithOrdinal("+=") || arg.StartsWith('='))
                                {
                                    concat = arg.Substring(0, arg.IndexOf('=') + 1);
                                    arg = arg.Substring(concat.Length);
                                }
                                bool isPath = false;
                                foreach (string pswitch in PATH_SWITCHES)
                                {
                                    if (pswitch == op)
                                    {
                                        if (op.EndsWithOrdinal("namespace"))
                                        {
                                            int sp = arg.IndexOf(' ');
                                            if (sp > 0)
                                            {
                                                concat += arg.Substring(0, sp) + ";";
                                                arg = arg.Substring(sp + 1).TrimStart();
                                            }
                                        }
                                        isPath = true;
                                        // remove quotes
                                        if (arg.StartsWith('\"') && arg.EndsWith('\"'))
                                            arg = arg.Substring(1, arg.Length - 2);

                                        if (!arg.StartsWith('\\') && !Path.IsPathRooted(arg))
                                            arg = Path.Combine(buildPath, arg);
                                    }
                                }
                                if (op == "-o" || op == "-output")
                                {
                                    builtSWF = arg;
                                    hasOutput = true;
                                }
                                if (!isPath) arg = arg.Replace(' ', ';');
                                cmd += op + concat + arg + ";";
                            }
                            else cmd += op + ";";
                        }
                    }
                }
                catch
                {
                    ErrorManager.ShowInfo(TextHelper.GetString("Info.InvalidForQuickBuild"));
                }
            }
            else if (requireTag) return;

            // Flex4 static linking
            if (isFlex4SDK && !cmd.Contains("-static-link-runtime-shared-libraries"))
                cmd += ";-static-link-runtime-shared-libraries=true";

            // add current class sourcepath and global classpaths
            cmd += ";-sp+=" + theFile.BasePath;
            if (ASContext.Context.Settings.UserClasspath != null)
                foreach (string cp in ASContext.Context.Settings.UserClasspath)
                    cmd += ";-sp+=" + cp;
            // add output filename
            if (!hasOutput) 
            {
                builtSWF = Path.Combine(buildPath, Path.GetFileNameWithoutExtension(filename)+".swf");
                cmd = "-o;" + builtSWF + ";" + cmd;
            }
            // add current file
            cmd += ";--;" + filename;

            // build
            cmd = cmd.Replace(";;", ";");
            RunMxmlc(cmd, flex2Path);
            if (!playAfterBuild) builtSWF = null;
            
            // restaure working directory
            Environment.CurrentDirectory = currentPath;
        }

        void CheckIsFlex4SDK(string flexPath)
        {
            if (checkedSDK == flexPath) return;
            checkedSDK = flexPath;

            string flexDesc = Path.Combine(flexPath, "flex-sdk-description.xml");
            if (File.Exists(flexDesc))
            {
                string src = File.ReadAllText(flexDesc);
                isFlex4SDK = src.IndexOfOrdinal("<version>4") > 0;
            }
            else isFlex4SDK = false;
        }

        #region Background process

        /// <summary>
        /// Stop background processes
        /// </summary>
        public void Stop()
        {
            try
            {
                if (ascRunner != null && ascRunner.IsRunning) ascRunner.KillProcess();
            }
            catch { }
            finally
            {
                ascRunner = null;
            }
            try
            {
                if (mxmlcRunner != null && mxmlcRunner.IsRunning) mxmlcRunner.KillProcess();
            }
            catch { }
            finally
            {
                mxmlcRunner = null;
            }
        }
        
        /// <summary>
        /// Start background process
        /// </summary>
        void StartAscRunner(string flexPath)
        {
            currentSDK = flexPath;
            if (ascRunner != null && ascRunner.IsRunning) ascRunner.KillProcess();

            string cmd = jvmConfig["java.args"]
                + " -classpath \"" + ascPath + ";" + flexShellsPath + "\" AscShell";
            TraceManager.Add(TextHelper.GetString("Info.StartAscRunner") + "\n" 
                + JvmConfigHelper.GetJavaEXE(jvmConfig) + " " + cmd, 0);
            // run asc shell
            ascRunner = new ProcessRunner();
            ascRunner.WorkingDirectory = Path.GetDirectoryName(ascPath);
            ascRunner.RedirectInput = true;
            ascRunner.Run(JvmConfigHelper.GetJavaEXE(jvmConfig), cmd, true);
            ascRunner.Output += ascRunner_Output;
            ascRunner.Error += ascRunner_Error;
            errorState = 0;
            Thread.Sleep(100);
        }
        
        /// <summary>
        /// Start background process
        /// </summary>
        void StartMxmlcRunner(string flexPath)
        {
            currentSDK = flexPath;
            if (mxmlcRunner != null && mxmlcRunner.IsRunning) mxmlcRunner.KillProcess();

            CheckIsFlex4SDK(flexPath);
            string shell = isFlex4SDK ? "Mxmlc4Shell" : "MxmlcShell";

            string cmd = jvmConfig["java.args"] 
                + " -classpath \"" + mxmlcPath + ";" + flexShellsPath + "\" " + shell;
            TraceManager.Add(TextHelper.GetString("Info.StartMxmlcRunner") + "\n"
                + JvmConfigHelper.GetJavaEXE(jvmConfig) + " " + cmd, -1);
            // run compiler shell
            mxmlcRunner = new ProcessRunner();
            mxmlcRunner.WorkingDirectory = Path.Combine(flexPath, "frameworks");
            mxmlcRunner.RedirectInput = true;
            mxmlcRunner.Run(JvmConfigHelper.GetJavaEXE(jvmConfig), cmd, true);
            mxmlcRunner.Output += mxmlcRunner_Output;
            mxmlcRunner.Error += mxmlcRunner_Error;
            errorState = 0;
            Thread.Sleep(100);
        }

        #endregion

        #region process output capture

        int errorState;
        string errorDesc;
        bool notificationSent;

        void ascRunner_Error(object sender, string line)
        {
            if (line.StartsWithOrdinal("[Compiler] Error"))
            {
                errorState = 1;
                errorDesc = line.Substring(10);
            }
            else if (errorState == 1)
            {
                line = line.Trim();
                var mErr = Regex.Match(line, @"(?<file>[^,]+), Ln (?<line>[0-9]+), Col (?<col>[0-9]+)");
                if (mErr.Success)
                {
                    var filename = mErr.Groups["file"].Value;
                    try 
                    {
                        if (File.Exists(filename))
                        {
                            filename = PathHelper.GetLongPathName(filename);
                        }
                    }
                    catch {}
                    errorDesc = $"{filename}:{mErr.Groups["line"].Value}: col: {mErr.Groups["col"].Value}: {errorDesc}";
                    ascRunner_OutputError(sender, errorDesc);
                }
                errorState++;
            }
            else if (errorState > 0)
            {
                if (line.IndexOfOrdinal("error found") > 0) errorState = 0;
            }
            else if (line.Trim().Length > 0) ascRunner_OutputError(sender, line);
        }

        void ascRunner_OutputError(object sender, string line)
        {
            if (line is null) return;
            PluginBase.RunAsync(delegate
            {
                if (line.StartsWithOrdinal("Exception "))
                {
                    TraceManager.AddAsync(line, -3);
                    return;
                }
                if (silentChecking)
                {
                    SyntaxError?.Invoke(line);
                    return;
                }
                TraceManager.Add(line, -3);
                if (notificationSent) return;
                notificationSent = true;
                TraceManager.Add("Done(1)", -2);
                EventManager.DispatchEvent(this, new TextEvent(EventType.ProcessEnd, "Done(1)"));
                ASContext.SetStatusText(TextHelper.GetString("Info.AscDone"));
                EventManager.DispatchEvent(this, new DataEvent(EventType.Command, "ResultsPanel.ShowResults", null));
            });
        }

        void ascRunner_Output(object sender, string line)
        {
            if (line.StartsWithOrdinal("(ash)"))
            {
                if (line.IndexOfOrdinal("Done") > 0)
                {
                    running = false;
                    if (!silentChecking && !notificationSent)
                    {
                        notificationSent = true;
                        ascRunner_End();
                    }
                }
                return;
            }

            if (!silentChecking) TraceManager.AddAsync(line, 0);
        }

        static void ascRunner_End()
        {
            TraceManager.AddAsync("Done(0)", -2);
        }

        static void mxmlcRunner_Error(object sender, string line)
        {
            TraceManager.AddAsync(line, -3);
        }

        void mxmlcRunner_Output(object sender, string line)
        {
            PluginBase.RunAsync(delegate
            {
                if (!notificationSent && line.StartsWithOrdinal("Done("))
                {
                    running = false;
                    TraceManager.Add(line, -2);
                    notificationSent = true;
                    ASContext.SetStatusText(TextHelper.GetString("Info.MxmlcDone"));
                    EventManager.DispatchEvent(this, new TextEvent(EventType.ProcessEnd, line));
                    if (Regex.IsMatch(line, "Done\\([1-9]"))
                    {
                        EventManager.DispatchEvent(this, new DataEvent(EventType.Command, "ResultsPanel.ShowResults", null));
                    }
                    else RunAfterBuild();
                }
                else TraceManager.Add(line, 0);
            });
        }

        void RunAfterBuild()
        {
            if (builtSWF is null || !File.Exists(builtSWF))
            {
                debugMode = false;
                return;
            }
            string swf = builtSWF;
            builtSWF = null;

            // debugger
            if (debugMode)
            {
                DataEvent de = new DataEvent(EventType.Command, "AS3Context.StartDebugger", null);
                EventManager.DispatchEvent(this, de);
            }

            // other plugin may handle the SWF playing
            DataEvent dePlay = new DataEvent(EventType.Command, "FlashViewer.Default", swf);
            EventManager.DispatchEvent(this, dePlay);
            if (dePlay.Handled) return;
            
            try 
            {
                // change current directory
                string currentPath = Directory.GetCurrentDirectory();
                Directory.SetCurrentDirectory(Path.GetDirectoryName(swf));
                // run
                Process.Start(swf);
                // restaure current directory
                Directory.SetCurrentDirectory(currentPath);
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError(ex.Message, ex);
            }
        }
        #endregion

    }
}
