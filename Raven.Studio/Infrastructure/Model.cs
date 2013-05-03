﻿using System;
using System.Net;
using System.Threading.Tasks;
using Raven.Abstractions;
using Raven.Abstractions.Extensions;
using Raven.Studio.Commands;
using Raven.Studio.Models;

namespace Raven.Studio.Infrastructure
{
	public abstract class Model : NotifyPropertyChangedBase
	{
		private Task currentTask;
		private DateTime lastRefresh;
		protected bool IsForced;
		protected TimeSpan RefreshRate { get; set; }

		protected Model()
		{
			RefreshRate = TimeSpan.FromSeconds(5);
		}

		internal void ForceTimerTicked()
		{
			IsForced = true;
			TimerTicked();
		}

		internal void TimerTicked()
		{
			if (ApplicationModel.Current.Server.Value.CreateNewDatabase)
			{
				ApplicationModel.Current.Server.Value.CreateNewDatabase = false;
				ApplicationModel.Current.Server.Value.DocumentStore
					.AsyncDatabaseCommands
					.ForSystemDatabase()
					.GetAsync("Raven/StudioConfig")
					.ContinueWith(task =>
					{
						if (task.IsFaulted == false)
						{
							Execute.OnTheUI(() =>
							{
								if (task.Result != null && task.Result.DataAsJson.ContainsKey("WarnWhenUsingSystemDatabase"))
								{
									if (task.Result.DataAsJson.Value<bool>("WarnWhenUsingSystemDatabase") == false)
										return;
								}
								Command.ExecuteCommand(new CreateDatabaseCommand());
							});
						}
						else
						{
							GC.KeepAlive(task.Exception); // ignoring the exeption
						}
					});
			}

			ApplicationModel.Current.UpdateAlerts();

			if (currentTask != null)
				return;

			lock (this)
			{
				if (currentTask != null)
					return;

				var timeFromLastRefresh = SystemTime.UtcNow - lastRefresh;
				var refreshRate = GetRefreshRate();
				if (timeFromLastRefresh < refreshRate)
					return;

				using(OnWebRequest(request => request.Headers["Raven-Timer-Request"] = "true"))
					currentTask = TimerTickedAsync();

				if (currentTask == null)
					return;

				currentTask
					.Catch()
					.Finally(() =>
					{
						lastRefresh = SystemTime.UtcNow;
						IsForced = false;
						currentTask = null;
					});
			}
		}

		private TimeSpan GetRefreshRate()
		{
			if (IsForced)
				return TimeSpan.FromSeconds(0.9);
			/*if (Debugger.IsAttached)
				return RefreshRate.Add(TimeSpan.FromSeconds(60));*/
			return RefreshRate;
		}

		public virtual Task TimerTickedAsync()
		{
			return null;
		}

	    [ThreadStatic] 
		protected static Action<WebRequest> onWebRequest;

		public static IDisposable OnWebRequest(Action<WebRequest> action)
		{
			onWebRequest += action;
			return new DisposableAction(() => onWebRequest = null);
		}
	}
}