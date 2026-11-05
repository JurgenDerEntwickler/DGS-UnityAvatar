using System;
using System.Collections.Generic;
using UnityEngine;

public class UnityMainThreadDispatcher : MonoBehaviour
{
   private static readonly Queue<Action> _executionQueue = new Queue<Action>();
   private static UnityMainThreadDispatcher _instance;

   public static void Enqueue(Action action)
   {
       lock (_executionQueue)
       {
           _executionQueue.Enqueue(action);
       }
   }

   void Awake()
   {
       if (_instance == null)
           _instance = this;
       else
           Destroy(gameObject);

       DontDestroyOnLoad(gameObject);
   }

   void Update()
   {
       lock (_executionQueue)
       {
           while (_executionQueue.Count > 0)
           {
               _executionQueue.Dequeue()?.Invoke();
           }
       }
   }
}