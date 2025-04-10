using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;
using UnityEngine.Profiling;
using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using Debug = UnityEngine.Debug;
using UnityEditor.ShaderGraph.Internal;
using Unity.Profiling;

public class ParallelTaskBenchmark : MonoBehaviour
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern uint GetCurrentProcessorNumber();

    public Vector3[] results_parallaxTask = new Vector3[240];       
    public Vector3[] results_job = new Vector3[240];        //�ʱ�

    const int TotalCount = 240;
    const int TaskCount = 4;
    const int ChunkSize = TotalCount / TaskCount;

    public struct VecWithIndex
    {
        public int index;
        public Vector3 vec;

        public VecWithIndex(int i, Vector3 v)
        {
            index = i;
            vec = v;
        }
    }

    void Start()
    {
        StartCoroutine(RunParallelTaskBenchmark());
        //StartCoroutine(RunJobCoroutine());
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Keypad9))
        {
            StartCoroutine(RunParallelTaskBenchmark());
        }
    }

    public static float A(int i)
    {
        var random = new System.Random(i);
        return (float)(random.NextDouble());
    }
    private static void ParallelCal(int taskIndex, int ChunkSize, Channel<VecWithIndex> channel)
    {
        // Parallel.For�� ���� ������ Ǯ ���� ���� CPU �ھ �����Ͽ� ���ķ� ó���մϴ�.
        Parallel.For(0, ChunkSize, i =>
        {
            int globalIndex = taskIndex * ChunkSize + i;
            float x = A(globalIndex) * 22f - 11f;
            float y = A(globalIndex + 1) * 22f - 11f;
            float z = A(globalIndex + 2) * 22f - 11f;

            channel.Writer.TryWrite(new VecWithIndex(globalIndex, new Vector3(x, y, z))); // Channel�� �� ����

            // �� �������� Managed ID, �� CPU (OS ������ ����) �α� ���
            //var currentThreadId = Thread.CurrentThread.ManagedThreadId;
            //int processorId = -1;
            try
            {
                //processorId = (int)GetCurrentProcessorNumber();
            }
            catch { }

            //Debug.Log($"[Thread ID: {currentThreadId}] [Vec Index: {globalIndex}] [CPU ID: {processorId}]");
        });
    }
    IEnumerator RunParallelTaskBenchmark()
    {
        //Profiler.BeginSample("ParallaxTaskBenchmark");
        var gcRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");

        Debug.Log("�� Parallax + Task ����");
        Stopwatch sw = Stopwatch.StartNew();

        // Channel�� ���� �۾�(Task)�� ������ �����͸� �񵿱������� �Һ���(Coroutine)���� �����ϱ� ���� ����
        // ThreadSafe�� + Reader/Writer ��, �񵿱�(await) ��Ʈ���� ����ȭ
        // CreateUnbounded�� ũ�� ���� ���� => ������ �߻��� ���� ----> CreateBounded(Capacity)��� ����
        // NativeArray�̳� class + ArrayPool<class>�� ���¸� ��� ���� => ������ ����
        //ArrayPool<VecWithIndex> (class)	NativeArray<VecWithIndex> (struct)
        Channel<VecWithIndex> channel = Channel.CreateBounded<VecWithIndex>(240);
        Task[] taskList = new Task[TaskCount];

        TaskScheduler scheduler = TaskScheduler.FromCurrentSynchronizationContext();        //���ؽ�Ʈ�� ���ν������ ����ȭ

        for (int t = 0; t < TaskCount; t++)
        {
            int taskIndex = t;//������ ����
            Profiler.BeginSample("Delegate");

            taskList[t] = Task.Factory.StartNew(() =>
            {
                // Parallel.For�� ���� ������ Ǯ ���� ���� CPU �ھ �����Ͽ� ���ķ� ó���մϴ�.
                Parallel.For(0, ChunkSize, i =>
                {
                    int globalIndex = taskIndex * ChunkSize + i;
                    //float x = A(globalIndex) * 22f - 11f;
                    //float y = A(globalIndex + 1) * 22f - 11f;
                    //float z = A(globalIndex + 2) * 22f - 11f;

                    //channel.Writer.TryWrite(new VecWithIndex(globalIndex, new Vector3(x, y, z))); // Channel�� �� ����

                    // �� �������� Managed ID, �� CPU (OS ������ ����) �α� ���
                    var currentThreadId = Thread.CurrentThread.ManagedThreadId;
                    int processorId = -1;
                    try
                    {
                        processorId = (int)GetCurrentProcessorNumber();
                    }
                    catch { }

                    Debug.Log($"[Thread ID: {currentThreadId}] [Vec Index: {globalIndex}] [CPU ID: {processorId}]");
                });
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);//scheduler);
            Profiler.EndSample();
        }

        // ArrayPool: GC �� �Ҵ� ���� �޸� ������ ���� Ǯ (CLR���� ����)
        // ���� ���������δ� ���� object pool�� �־� GC Gen0 ȸ�� ����
        Vector3[] pooledArray = ArrayPool<Vector3>.Shared.Rent(TotalCount); // �ʿ� ������ŭ ������
        int insertIndex = 0;

        // Reader ó���� ���� Task�� �����Ͽ� Coroutine���� await ����
        /*
        Task readerTask = Task.Run(async () =>
        {
            int i = 0;
            while (await channel.Reader.WaitToReadAsync())      //ä�ο� ���� ���� ���� ������ �񵿱� ���, Complete�� ȣ��Ǹ� false��ȯ
            {
                while (channel.Reader.TryRead(out var pair))        //���ΰ� ��������� false
                {
                    i = pair.index;
                    if (i < TotalCount)
                        pooledArray[i] = pair.vec;
                }
            }
        });
        */
        yield return new WaitUntil(() => Task.WhenAll(taskList).IsCompleted);        // ��� Task�� �Ϸ�� ������ ���
        channel.Writer.Complete();                      // ä�� �ۼ��� ����
        //yield return new WaitUntil(() => readerTask.IsCompleted);                       // channel�� read ����
       
        Array.Copy(pooledArray, results_parallaxTask, TotalCount);
        ArrayPool<Vector3>.Shared.Return(pooledArray); // Ǯ�� ��ȯ�Ͽ� ���� �����ϰ� ��

        sw.Stop();
        //Debug.Log($"? Parallax + Task �Ϸ�: {sw.ElapsedMilliseconds} ms");
        //Profiler.EndSample();

        foreach (var task in taskList)
        {
            task.Dispose(); // Task ����
        }
        //readerTask.Dispose(); // Reader Task ����
        sw = null;
        channel = null;
        /*
        long memory = GC.GetTotalMemory(false);
        Debug.Log($"?? GC Memory ��뷮: {memory / 1024f:N2} KB");
        // GC �޸� ��� (������ �� �Ҵ緮)
        if (gcRecorder.Valid)
        {
            long gcBytes = gcRecorder.LastValue;
            Debug.Log($"ProfilerRecorder - GC Allocated In Frame: {gcBytes / 1024f:N2} KB");
        }
        gcRecorder.Dispose();

        Debug.Log("?? �ڷ�ƾ ����");
        */
    }

    IEnumerator RunJobCoroutine()
    {
        Debug.Log("�� Job + Burst ����");
        NativeArray<Vector3> jobResult = new NativeArray<Vector3>(TotalCount, Allocator.TempJob);

        JobVector3Generator job = new JobVector3Generator
        {
            results = jobResult,
            seed = (int)(Time.realtimeSinceStartup * 1000)
        };

        Stopwatch sw = Stopwatch.StartNew();
        JobHandle handle = job.Schedule(TotalCount, 32); // IJobParallelFor ���� �����ٸ�

        yield return new WaitUntil(() => handle.IsCompleted == true);
        handle.Complete();
        sw.Stop();
        jobResult.CopyTo(results_job);
        jobResult.Dispose();

        Debug.Log($"? Job �Ϸ�: {sw.ElapsedMilliseconds} ms");
    }

    [BurstCompile]
    struct JobVector3Generator : IJobParallelFor
    {
        public NativeArray<Vector3> results;
        public int seed;

        public void Execute(int index)
        {
            float x = UnityEngine.Random.Range(-11f, 11f) + index;
            float y = UnityEngine.Random.Range(-11f, 11f) + index + 1;
            float z = UnityEngine.Random.Range(-11f, 11f) + index + 2;
            results[index] = new Vector3(x, y, z);
        }
    }
}
