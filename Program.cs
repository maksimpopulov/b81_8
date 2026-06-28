using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Lab08
{
    public class procEventArgs : EventArgs
    {
        public int id { get; set; }
    }

    public class Client
    {
        private Server server;
        public event EventHandler<procEventArgs> request;

        public Client(Server server)
        {
            this.server = server;
            this.request += server.proc;
        }

        public void send(int id)
        {
            procEventArgs args = new procEventArgs { id = id };
            OnProc(args);
        }

        protected virtual void OnProc(procEventArgs e)
        {
            EventHandler<procEventArgs> handler = request;
            handler?.Invoke(this, e);
        }
    }

    public class Server
    {
        private struct PoolRecord
        {
            public Thread thread;
            public bool in_use;
        }

        private readonly int n;
        private readonly double mu;
        private readonly Random rand;
        private readonly object threadLock = new object();
        private PoolRecord[] pool;
        private int requestCount = 0;
        private int processedCount = 0;
        private int rejectedCount = 0;
        private int activeThreads = 0;

        public int RequestCount => requestCount;
        public int ProcessedCount => processedCount;
        public int RejectedCount => rejectedCount;
        public int ActiveThreads => activeThreads;

        public Server(int channels, double serviceRate)
        {
            n = channels;
            mu = serviceRate;
            rand = new Random();
            pool = new PoolRecord[n];
            for (int i = 0; i < n; i++)
                pool[i].in_use = false;
        }

        public void proc(object sender, procEventArgs e)
        {
            lock (threadLock)
            {
                requestCount++;
                for (int i = 0; i < n; i++)
                {
                    if (!pool[i].in_use)
                    {
                        pool[i].in_use = true;
                        pool[i].thread = null;
                        Thread t = new Thread(Answer);
                        pool[i].thread = t;
                        Interlocked.Increment(ref activeThreads);
                        processedCount++;
                        t.Start(e.id);
                        return;
                    }
                }
                rejectedCount++;
            }
        }

        private void Answer(object arg)
        {
            int id = (int)arg;
            double serviceTime = -Math.Log(1 - NextDouble()) / mu;
            Thread.Sleep((int)(serviceTime * 1000));

            lock (threadLock)
            {
                for (int i = 0; i < n; i++)
                {
                    if (pool[i].thread == Thread.CurrentThread)
                    {
                        pool[i].in_use = false;
                        pool[i].thread = null;
                        break;
                    }
                }
            }
            Interlocked.Decrement(ref activeThreads);
        }

        private double NextDouble()
        {
            lock (rand)
            {
                return rand.NextDouble();
            }
        }

        public int GetBusyChannelsCount()
        {
            lock (threadLock)
            {
                return pool.Count(r => r.in_use);
            }
        }
    }

    public static class Program
    {
        private const int TotalRequests = 50;
        private const int Runs = 5;
        private const int Channels = 5;
        private const double Mu = 2.0;
        private static readonly double[] Lambdas = Enumerable.Range(1, 11).Select(i => i * 2.0).ToArray();

        private struct ExpResult
        {
            public double P0, Pn, Q, A, K;
        }

        private static (double P0, double Pn, double Q, double A, double K) Theor(double lambda, double mu, int n)
        {
            double rho = lambda / mu;
            double sum = 0;
            for (int i = 0; i <= n; i++)
                sum += Math.Pow(rho, i) / Factorial(i);
            double P0 = 1.0 / sum;
            double Pn = Math.Pow(rho, n) / Factorial(n) * P0;
            double Q = 1 - Pn;
            double A = lambda * Q;
            double K = A / mu;
            return (P0, Pn, Q, A, K);
        }

        private static int Factorial(int x)
        {
            int res = 1;
            for (int i = 2; i <= x; i++) res *= i;
            return res;
        }

        private static ExpResult RunExperiment(double lambda)
        {
            double sumP0 = 0, sumPn = 0, sumQ = 0, sumA = 0, sumK = 0;

            for (int run = 0; run < Runs; run++)
            {
                var server = new Server(Channels, Mu);
                var client = new Client(server);
                var busySamples = new List<int>();
                bool monitoring = true;
                var monitorThread = new Thread(() =>
                {
                    while (monitoring)
                    {
                        busySamples.Add(server.GetBusyChannelsCount());
                        Thread.Sleep(10);
                    }
                });
                monitorThread.Start();

                var rand = new Random();
                for (int i = 1; i <= TotalRequests; i++)
                {
                    client.send(i);
                    if (i < TotalRequests)
                    {
                        double interval = -Math.Log(1 - rand.NextDouble()) / lambda;
                        Thread.Sleep((int)(interval * 1000));
                    }
                }

                while (server.ActiveThreads > 0)
                    Thread.Sleep(10);

                monitoring = false;
                monitorThread.Join();

                double totalTime = TotalRequests / lambda;

                int req = server.RequestCount;
                int proc = server.ProcessedCount;
                int rej = server.RejectedCount;
                double Pn_exp = (double)rej / req;
                double Q_exp = (double)proc / req;
                double A_exp = proc / totalTime;
                double K_exp = A_exp / Mu;
                double P0_exp = busySamples.Count > 0 ? busySamples.Count(b => b == 0) / (double)busySamples.Count : 0;

                sumP0 += P0_exp;
                sumPn += Pn_exp;
                sumQ += Q_exp;
                sumA += A_exp;
                sumK += K_exp;
            }

            return new ExpResult
            {
                P0 = sumP0 / Runs,
                Pn = sumPn / Runs,
                Q = sumQ / Runs,
                A = sumA / Runs,
                K = sumK / Runs
            };
        }

        private static void SaveCsv(string filePath, string title,
            double[] xValues, double[] theorValues, double[] expValues)
        {
            using (var writer = new StreamWriter(filePath))
            {
                writer.WriteLine($"# {title}");
                writer.WriteLine("Lambda,Theoretical,Experimental");
                for (int i = 0; i < xValues.Length; i++)
                {
                    writer.WriteLine($"{xValues[i]:F2},{theorValues[i]:F6},{expValues[i]:F6}");
                }
            }
        }

        public static void Main()
        {
            Console.WriteLine("Моделирование многоканальной СМО с отказами");
            Console.WriteLine($"Число каналов n = {Channels}");
            Console.WriteLine($"Интенсивность обслуживания μ = {Mu} заявок/сек");
            Console.WriteLine($"Количество заявок в прогоне = {TotalRequests}, число прогонов = {Runs}");
            Console.WriteLine("Исследуемые интенсивности λ: " + string.Join(", ", Lambdas));

            var lambdaList = new List<double>();
            var theorP0 = new List<double>(); var expP0 = new List<double>();
            var theorPn = new List<double>(); var expPn = new List<double>();
            var theorQ = new List<double>(); var expQ = new List<double>();
            var theorA = new List<double>(); var expA = new List<double>();
            var theorK = new List<double>(); var expK = new List<double>();

            var reportLines = new List<string>();
            reportLines.Add("λ\tТеор.P0\tЭксп.P0\tТеор.Pn\tЭксп.Pn\tТеор.Q\tЭксп.Q\tТеор.A\tЭксп.A\tТеор.K\tЭксп.K");

            foreach (double lambda in Lambdas)
            {
                Console.WriteLine($"\nОбработка λ = {lambda:F2} ...");
                var theor = Theor(lambda, Mu, Channels);
                var exp = RunExperiment(lambda);

                lambdaList.Add(lambda);
                theorP0.Add(theor.P0); expP0.Add(exp.P0);
                theorPn.Add(theor.Pn); expPn.Add(exp.Pn);
                theorQ.Add(theor.Q); expQ.Add(exp.Q);
                theorA.Add(theor.A); expA.Add(exp.A);
                theorK.Add(theor.K); expK.Add(exp.K);

                reportLines.Add($"{lambda:F2}\t{theor.P0:F4}\t{exp.P0:F4}\t{theor.Pn:F4}\t{exp.Pn:F4}\t{theor.Q:F4}\t{exp.Q:F4}\t{theor.A:F4}\t{exp.A:F4}\t{theor.K:F4}\t{exp.K:F4}");
            }

            Directory.CreateDirectory("result");
            File.WriteAllLines("result/results.txt", reportLines);
            Console.WriteLine("\nОтчёт сохранён в result/results.txt");

            double[] x = lambdaList.ToArray();

            // Сохраняем данные для графиков в CSV
            SaveCsv("result/p-1.csv", "Вероятность простоя P0", x, theorP0.ToArray(), expP0.ToArray());
            SaveCsv("result/p-2.csv", "Вероятность отказа Pn", x, theorPn.ToArray(), expPn.ToArray());
            SaveCsv("result/p-3.csv", "Относительная пропускная способность Q", x, theorQ.ToArray(), expQ.ToArray());
            SaveCsv("result/p-4.csv", "Абсолютная пропускная способность A", x, theorA.ToArray(), expA.ToArray());
            SaveCsv("result/p-5.csv", "Среднее число занятых каналов k", x, theorK.ToArray(), expK.ToArray());

            Console.WriteLine("\nCSV файлы для графиков сохранены в папку result/");
            Console.WriteLine("Для построения графиков используйте Excel, Google Sheets или Python");

            // Вывод таблицы результатов в консоль
            Console.WriteLine("\n=== РЕЗУЛЬТАТЫ ===");
            Console.WriteLine("λ\tP0(теор)\tP0(эксп)\tPотк(теор)\tPотк(эксп)\tQ(теор)\tQ(эксп)");
            Console.WriteLine("----------------------------------------------------------------");
            for (int i = 0; i < lambdaList.Count; i++)
            {
                Console.WriteLine($"{lambdaList[i]:F2}\t{theorP0[i]:F4}\t\t{expP0[i]:F4}\t\t{theorPn[i]:F4}\t\t{expPn[i]:F4}\t\t{theorQ[i]:F4}\t{expQ[i]:F4}");
            }

            Console.WriteLine("\nНажмите любую клавишу для выхода...");
            Console.ReadKey();
        }
    }
}