﻿using Newtonsoft.Json.Linq;
using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace FillDownload
{
    class FillDownloadThread
    {
        Thread m_thread;
        Boolean m_running = false;
        TimeSpan m_startTime = default(TimeSpan);
        TimeSpan m_endTime = default(TimeSpan);
        TimeSpan m_interval = default(TimeSpan);
        DateTime m_startDate = default(DateTime);
        DateTime m_minTimeStamp = default(DateTime);
        bool[] m_daysToRun;

        object m_lock = new object();

        public FillDownloadThread(TimeSpan start_time, TimeSpan end_time, TimeSpan interval, bool[] days_to_run)
            :this(start_time, end_time, interval, days_to_run, DateTime.Today)
        {
        }

        public FillDownloadThread(TimeSpan start_time, TimeSpan end_time, TimeSpan interval, bool[] days_to_run, DateTime start_date)
        {
            m_startTime = start_time;
            m_endTime = end_time;
            m_interval = interval;
            m_startDate = start_date;
            m_daysToRun = days_to_run;
            m_minTimeStamp = m_startDate;
        }

        public void Start()
        {
            try
            { 
                CommonEnums.BuildEnums();
            }
            catch (Exception e)
            {
                RaiseErrorEvent("Error: " + e.Message + e.StackTrace);
                return;
            }

            m_thread = new Thread(this.ThreadMain);
            m_thread.Name = "Fill Download Thread";
            m_running = true;
            m_thread.Start();
        }

        void ThreadMain()
        {
            while (m_running)
            {
                ThreadWait(GetNextDownloadPeriod());

                while (m_running && DateTime.Now < DateTime.Today + m_endTime)
                {
                    try
                    {
                        DownloadFills();
                    }
                    catch(Exception e)
                    {
                        RaiseErrorEvent("Error: " + e.Message + e.StackTrace);
                        m_running = false;
                        break;  
                    }

                    ThreadWait(m_interval);
                }
            }
        }

        private void DownloadFills()
        {
            // Perform REST request/response to download fill data, specifying our cached minimum timestamp as a starting point.  
            // On a successful response the timestamp will be updated so we run no risk of downloading duplicate fills.

            List<TT_Fill> fills = new List<TT_Fill>();
            do
            {
                var min_param = new RestSharp.Parameter("minTimestamp", TT_Info.ToRestTimestamp(m_minTimeStamp).ToString(), RestSharp.ParameterType.QueryString);

                RestSharp.IRestResponse result = RestManager.GetRequest("ledger", "fills", min_param);

                if (result.StatusCode != System.Net.HttpStatusCode.OK)
                    throw new Exception("Request for fills unsuccessful. Status:" + result.StatusCode.ToString() + "Error Message: " + result.ErrorMessage);

                JObject json_data = JObject.Parse(result.Content);
                foreach (var fill in json_data["fills"])
                {
                    fills.Add(new TT_Fill(fill));
                }

                fills.Sort((f1, f2) => f1.UtcTimeStamp.CompareTo(f2.UtcTimeStamp));
                RaiseFillDownloadEvent(fills);

                if (fills.Count > 0 && m_running)
                    m_minTimeStamp = new DateTime(fills[fills.Count - 1].UtcTimeStamp.Ticks + 1);
            }
            while (fills.Count == TT_Info.MAX_RESPONSE_FILLS);
        }


        public delegate void FillDownloadEventHandler(object sender, List<TT_Fill> fills);
        public event FillDownloadEventHandler FillDownload;
        private void RaiseFillDownloadEvent(List<TT_Fill> fills)
        {
            if (FillDownload != null)
                FillDownload(this, fills);
        }

        public delegate void DownloadThreadErrorHandler(object sender, string error_message);
        public event DownloadThreadErrorHandler OnError;
        private void RaiseErrorEvent(string error_message)
        {
            if (OnError != null)
                OnError(this, error_message);
        }

        TimeSpan GetNextDownloadPeriod()
        {
            // Return the time from now until the next scheduled download period 
            // or now if we are already in one.

            int day_of_week = (int)DateTime.Today.DayOfWeek;
            int next_day = (day_of_week + 1) % 7;

            if(m_daysToRun[day_of_week] == true && DateTime.Now.TimeOfDay > m_startTime && DateTime.Now.TimeOfDay < m_endTime)
            {
                return default(TimeSpan);
            }
            else
            {
                int days_until;

                if(m_daysToRun[day_of_week] == true && DateTime.Now.TimeOfDay < m_startTime)
                {
                    days_until = 0;
                }
                else
                {

                    for (; next_day != day_of_week; next_day = (next_day + 1) % 7)
                    {
                        if (m_daysToRun[next_day] == true)
                            break;
                    }

                    days_until = ((next_day - day_of_week + 7) % 7);

                    if(days_until == 0)
                        days_until = 7;
                }

                DateTime nextStart = DateTime.Today.Date.AddDays(days_until) + m_startTime;
                return nextStart - DateTime.Now;
            }
        }

        public void StopThread()
        {
            if (m_thread != null)
            {
                m_running = false;
                ThreadNotify();
                m_thread.Join();
            }
        }

        private void ThreadWait(TimeSpan interval)
        {
            lock(m_lock)
            {
                if(m_running)
                {
                    Monitor.Wait(m_lock, interval);
                }
            }
        }

        private void ThreadNotify()
        {
            lock(m_lock)
            {
                Monitor.Pulse(m_lock);
            }
        }
    }
}