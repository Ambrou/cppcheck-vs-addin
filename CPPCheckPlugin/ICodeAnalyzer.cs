﻿using System.Collections.Generic;
using System;
using EnvDTE;
using System.Diagnostics;

namespace VSPackage.CPPCheckPlugin
{
	public abstract class ICodeAnalyzer : IDisposable
	{
		public enum SuppressionScope
		{
			suppressThisMessage,
			suppressThisMessageSolutionWide,
			suppressThisMessageGlobally,
			suppressThisTypeOfMessageFileWide,
			suppressThisTypeOfMessageProjectWide,
			suppressThisTypeOfMessagesSolutionWide,
			suppressThisTypeOfMessagesGlobally,
			suppressAllMessagesThisFileProjectWide,
			suppressAllMessagesThisFileSolutionWide,
			suppressAllMessagesThisFileGlobally
		};

		public enum SuppressionStorage
		{
			Project,
			Solution,
			Global
		}

		public enum AnalysisType { DocumentSavedAnalysis, ProjectAnalysis };

		protected ICodeAnalyzer()
		{
			_numCores = Environment.ProcessorCount;
		}

		~ICodeAnalyzer()
		{
			Dispose(false);
		}

		public abstract void analyze(List<SourceFile> filesToAnalyze, OutputWindowPane outputPane, bool is64bitConfiguration,
			bool isDebugConfiguration);

		public abstract void suppressProblem(Problem p, SuppressionScope scope);

		protected abstract HashSet<string> readSuppressions(SuppressionStorage storage, string projectBasePath = null, string projectName = null);

		protected abstract List<Problem> parseOutput(String output);

		protected void run(string analyzerExePath, string arguments, OutputWindowPane outputPane)
		{
			_outputPane = outputPane;

			abortThreadIfAny();
			MainToolWindow.Instance.clear();
			_thread = new System.Threading.Thread(() => analyzerThreadFunc(analyzerExePath, arguments));
			_thread.Name = "cppcheck";
			_thread.Start();
		}

		private void addProblemsToToolwindow(List<Problem> problems)
		{
			if (MainToolWindow.Instance == null)
				return;
			
			foreach(var problem in problems)
				MainToolWindow.Instance.displayProblem(problem);
		}

		protected string suppressionsFilePathByStorage(SuppressionStorage storage, string projectBasePath = null, string projectName = null)
		{
			switch (storage)
			{
				case SuppressionStorage.Global:
					return globalSuppressionsFilePath();
				case SuppressionStorage.Solution:
					return solutionSuppressionsFilePath();
				case SuppressionStorage.Project:
					Debug.Assert(!String.IsNullOrWhiteSpace(projectBasePath) && !String.IsNullOrWhiteSpace(projectName));
					return projectSuppressionsFilePath(projectBasePath, projectName);
				default:
					throw new InvalidOperationException("Unsupported enum value: " + storage.ToString());
			}
		}

		protected string suppressionsFilePathByScope(SuppressionScope scope, string projectBasePath = null, string projectName = null)
		{
			if (scope == SuppressionScope.suppressThisTypeOfMessagesGlobally || scope == SuppressionScope.suppressAllMessagesThisFileGlobally || scope == SuppressionScope.suppressThisMessageGlobally)
				return globalSuppressionsFilePath();
			if (scope == SuppressionScope.suppressThisMessageSolutionWide || scope == SuppressionScope.suppressThisTypeOfMessagesSolutionWide || scope == SuppressionScope.suppressAllMessagesThisFileSolutionWide)
				return solutionSuppressionsFilePath();
			else
				return projectSuppressionsFilePath(projectBasePath, projectName);
		}

		private void abortThreadIfAny()
		{
			if (_thread != null)
			{
				try
				{
					_terminateThread = true;
					_thread.Join();
				}
				catch (Exception ex)
				{
					DebugTracer.Trace(ex);
				}
				_thread = null;
			}
		}

		private void analyzerThreadFunc(string analyzerExePath, string arguments)
		{
			System.Diagnostics.Process process = null;
			_terminateThread = false;
			try
			{
				Debug.Assert(!String.IsNullOrEmpty(analyzerExePath));
				Debug.Assert(!String.IsNullOrEmpty(arguments));
				Debug.Assert(_outputPane != null);

				process = new System.Diagnostics.Process();
				process.StartInfo.FileName = analyzerExePath;
				process.StartInfo.Arguments = arguments;
				process.StartInfo.CreateNoWindow = true;

				// Set UseShellExecute to false for output redirection.
				process.StartInfo.UseShellExecute = false;

				// Redirect the standard output of the command.
				process.StartInfo.RedirectStandardOutput = true;
				process.StartInfo.RedirectStandardError = true;

				// Set our event handler to asynchronously read the sort output.
				process.OutputDataReceived += new DataReceivedEventHandler(this.analyzerOutputHandler);
				process.ErrorDataReceived += new DataReceivedEventHandler(this.analyzerOutputHandler);

				_outputPane.OutputString("Starting analyzer with arguments: " + arguments + "\n");

				var timer = Stopwatch.StartNew();
				// Start the process.
				process.Start();
				process.PriorityClass = ProcessPriorityClass.Idle;

				// Start the asynchronous read of the sort output stream.
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				// Wait for analysis completion
				while (!process.WaitForExit(30))
				{
					if (_terminateThread)
					{
						// finally block will run anyway and do the cleanup
						return;
					}
				}
				timer.Stop();
				if (process.ExitCode != 0)
					_outputPane.OutputString(analyzerExePath + " has exited with code " + process.ExitCode.ToString() + "\n");
				else
				{
					double timeElapsed = Math.Round(timer.Elapsed.TotalSeconds, 3);
					_outputPane.OutputString("Analysis completed in " + timeElapsed.ToString() + " seconds\n");
				}
				process.Close();
				process = null;
			}
			catch (Exception ex)
			{
				DebugTracer.Trace(ex);
			}
			finally
			{
				if (process != null)
				{
					try
					{
						process.Kill();
					}
					catch (Exception ex)
					{
						DebugTracer.Trace(ex);
					}

					process.Dispose();
				}
			}
		}

		private void analyzerOutputHandler(object sendingProcess, DataReceivedEventArgs outLine)
		{
			if (_thread == null)
			{
				// We got here because the environment is shutting down, the current object was disposed and the thread was aborted.
				return;
			}
			String output = outLine.Data;
			if (!String.IsNullOrEmpty(output))
			{
				addProblemsToToolwindow(parseOutput(output));
				_outputPane.OutputString(output + "\n");
			}
		}

		private string solutionSuppressionsFilePath()
		{
			return CPPCheckPluginPackage.solutionPath() + "\\" + CPPCheckPluginPackage.solutionName() + "_solution_suppressions.cfg";
		}

		private string projectSuppressionsFilePath(string projectBasePath, string projectName)
		{
			Debug.Assert(!String.IsNullOrWhiteSpace(projectBasePath) && !String.IsNullOrWhiteSpace(projectName));
			return projectBasePath + "\\" + projectName + "_project_suppressions.cfg";
		}

		private string globalSuppressionsFilePath()
		{
			return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\CppcheckVisualStudioAddIn\\suppressions.cfg";
		}

		public void Dispose()
		{
			Dispose(true);
		}

		protected virtual void Dispose(bool disposing)
		{
			abortThreadIfAny();
		}

		protected String _projectBasePath = null; // Base path for a project currently being checked
		protected String _projectName     = null; // Name of a project currently being checked

		private OutputWindowPane _outputPane = null;
		protected int _numCores;

		private System.Threading.Thread _thread = null;
		private bool _terminateThread = false;
	}
}
