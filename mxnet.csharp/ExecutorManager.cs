﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using log4net;
using mxnet.csharp.metric;

namespace mxnet.csharp
{
    class DataParallelExecutorManager
    {

        private ILog logger;
        private List<Tuple<int, int>> slices;
        private List<string> arg_names;
        public List<string> param_names { get; }
        public List<List<NDArray>> param_arrays => execgrp.param_arrays;
        public List<List<NDArray>> grad_arrays => execgrp.grad_arrays;

        private List<string> aux_names;
        private List<Context> ctx
            ;

        private DataParallelExecutorGroup execgrp;
        private Symbol symbol;
        private Func<string, Symbol> sym_gen;
        private DataParallelExecutorGroup curr_execgrp;
        private Dictionary<string, DataParallelExecutorGroup> execgrp_bucket;


        public DataParallelExecutorManager(Symbol symbol, Func<string, Symbol> sym_gen, List<Context> ctx,
            IDataIter train_data, List<string> param_names, List<string> arg_names,
            List<string> aux_names, List<int> work_load_list, ILog logger)
        {
            if (logger == null)
            {
                logger = LogManager.GetLogger("");
            }
            this.logger = logger;

            var num_device = ctx.Count;
            logger.Info("Start training with " + string.Join("", ctx));

            if (work_load_list == null)
            {
                work_load_list = Enumerable.Repeat(1, num_device).ToList();
            }
            Util.assert(work_load_list.Count == num_device, "Invalid settings for work load. ");

            var slices = _split_input_slice(train_data.batch_size, work_load_list);

            this.slices = slices;

            this.arg_names = arg_names;
            this.param_names = param_names;
            this.aux_names = aux_names;
            this.ctx = ctx;

            this.execgrp = new DataParallelExecutorGroup(symbol, this.arg_names, this.param_names, this.ctx,
                this.slices, train_data);

            this.symbol = symbol;

            this.sym_gen = sym_gen;
            this.curr_execgrp = null;
            // this is set when data is loaded
            if (this.sym_gen != null)
            {
                this.execgrp_bucket = new Dictionary<string, DataParallelExecutorGroup>()
                {
                    {train_data.default_bucket_key,  this.execgrp }
                };
            }


        }

        private List<Tuple<int, int>> _split_input_slice(int batch_size, List<int> work_load_list)
        {
            var total_work_load = work_load_list.Sum();

            var batch_num_list =
                work_load_list.Select(work_load => Math.Round((double)work_load * batch_size / total_work_load)).ToList();
            var batch_num_sum = batch_num_list.Sum();

            if (batch_num_sum < batch_size)
            {
                batch_num_list[batch_num_list.Count - 1] += batch_size - batch_num_sum;
            }
            List<Tuple<int, int>> slices = new List<Tuple<int, int>>();
            int end = 0;

            foreach (var batch_num in batch_num_list)
            {
                var begin = (int)Math.Min(end, batch_size);
                end = (int)Math.Min(begin + batch_num, batch_size);

                if (begin >= end)
                    throw new Exception("Too many slices such that some splits are empty");
                slices.Add(Tuple.Create(begin, end));
            }

            return slices;
        }

        public void install_monitor(Monitor monitor)
        {
            if (this.sym_gen != null)
            {
                throw new Exception("Monitoring is not implemented for bucketing");
            }
            foreach (var train_execs in this.execgrp.train_execs)
            {
                monitor.Install(train_execs);
            }
        }

        public void set_params(Dictionary<string, NDArray> argParams, Dictionary<string, NDArray> auxParams)
        {
            foreach (var texec in execgrp.train_execs)
            {
                texec.copy_params_from(argParams, auxParams);
            }
     
        }

        public void load_data_batch(IDataBatch data_batch)
        {
            if (this.sym_gen != null)
            {
                var key = data_batch.bucket_key;
                if (!execgrp_bucket.ContainsKey(key))
                {
                    //create new bucket entry
                    var symbol = sym_gen(key);
                    var execgrp = new DataParallelExecutorGroup(symbol, this.arg_names,
                        this.param_names, this.ctx,
                        this.slices, data_batch,
                        shared_group: this.execgrp);
                    this.execgrp_bucket[key] = execgrp;
                }
                this.curr_execgrp = this.execgrp_bucket[key];
            }
            else
            {
                this.curr_execgrp = this.execgrp;
            }
            this.curr_execgrp.load_data_batch(data_batch);
        }
        /// <summary>
        /// run forward on the current executor
        /// </summary>
        /// <param name="is_train"></param>
        public void forward(bool is_train)
        {
            this.curr_execgrp.forward(is_train: is_train);
        }
        /// <summary>
        /// run backward on the current executor
        /// </summary>
        public void backward()
        {
            this.curr_execgrp.backward();
        }

        public void update_metric(EvalMetric metric, List<NDArray> labels)
        {
            this.curr_execgrp.update_metric(metric, labels);
        }
    }

    internal class DataParallelExecutorGroup
    {
        private List<Dictionary<string, NDArray>> shared_data_arrays;
        private List<string> data_names;
        private List<string> label_names;
        private IList<string> aux_names;
        private List<int> param_idx;
        private List<string> param_names;
        public List<Executor> train_execs { get; }
        private List<List<Tuple<Tuple<int, int>, NDArray>>> data_arrays;
        private List<List<Tuple<Tuple<int, int>, NDArray>>> label_arrays;
        public List<List<NDArray>> param_arrays { get; }
        public List<List<NDArray>> grad_arrays { get; }
        private List<List<NDArray>> aux_arrays;
        private List<Tuple<int, int>> slices;

        public DataParallelExecutorGroup(Symbol sym,
            List<string> arg_names,
            List<string> param_names,
            List<Context> ctx,
            List<Tuple<int, int>> slices,
            IDataIProvide train_data,
            DataParallelExecutorGroup shared_group = null)
        {
            Util._check_arguments(sym);

            if (shared_group == null)
            {
                this.shared_data_arrays = ctx.Select(s => new Dictionary<string, NDArray>()).ToList();
            }
            else
            {
                this.shared_data_arrays = shared_group.shared_data_arrays;
            }
            this.data_names = train_data.provide_data.Select(s => s.Key).ToList();
            this.label_names = train_data.provide_label.Select(s => s.Key).ToList();
            this.aux_names = sym.ListAuxiliaryStates();

            this.param_idx = arg_names.Select((x, i) => new { x = x, i = i }).Where(w => param_names.Contains(w.x)).Select(s => s.i).ToList();
            this.param_names = param_idx.Select(s => arg_names[s]).ToList();

            this.train_execs = new List<Executor>();
            for (int i = 0; i < ctx.Count; i++)
            {
                var concat = train_data.provide_data.Concat(train_data.provide_label);

                var data_shapes = concat.ToDictionary(kv_k => kv_k.Key,
                       kv_v =>
                       {
                           List<uint> tuple = new List<uint>();
                           tuple.Add((uint)(slices[i].Item2 - slices[i].Item1));
                           tuple.AddRange(kv_v.Value.data().Skip(1));
                           return tuple.ToArray();
                       });

                var shared_exec = shared_group == null ? null : shared_group.train_execs[i];

                var train_exec = _bind_exec(sym, ctx[i], data_shapes, this.param_names,
                    need_grad_input: true, base_exec: shared_exec,
                    shared_data_arrays: this.shared_data_arrays[i]);

                this.train_execs.Add(train_exec);
            }


            this.data_arrays = data_names.Select(name => train_execs.Select((e, i) => Tuple.Create(slices[i], e.arg_dict[name])).ToList()).ToList();
            this.label_arrays = label_names.Select(name => train_execs.Select((e, i) => Tuple.Create(slices[i], e.arg_dict[name])).ToList()).ToList();

            this.param_arrays = param_idx.Select(i => train_execs.Select((e) => e.arg_arrays[i]).ToList()).ToList();

            this.grad_arrays = param_idx.Select(i => train_execs.Select((e) => e.grad_arrays[i]).ToList()).ToList();

            this.aux_arrays = Enumerable.Range(0, this.aux_names.Count).Select(i => train_execs.Select((e) => e.aux_arrays[i]).ToList()).ToList();

            this.slices = slices;
        }

        private  Executor _bind_exec(Symbol sym, Context ctx, Dictionary<string, uint[]> input_shapes,
            List<string> param_names, bool need_grad_input, Executor base_exec,
            Dictionary<string, NDArray> shared_data_arrays, Dictionary<string, Type> input_types = null, ILog logger = null)
        {
            if (logger == null)
            {
                logger = LogManager.GetLogger("");
            }

            var arg_shapes = new List<uint[]>();
            var aux_shapes = new List<uint[]>();
            var out_shapes = new List<uint[]>();
            sym.InferShape(input_shapes, arg_shapes, aux_shapes, out_shapes);


            var arg_types = new List<Type>();
            var aux_type = new List<Type>();
            var out_type = new List<Type>();
            if (input_types == null)
            {
                input_types = input_shapes.ToDictionary(k => k.Key, v => typeof(float));
            }
            sym.InferType(input_types, arg_types, aux_type, out_type);

            var grad_arrays = need_grad_input ? new Dictionary<string, NDArray>() : null;

            var arg_names = sym.ListArguments();
            HashSet<string> need_grad = null;
            if (need_grad_input == false)
            {
                need_grad = new HashSet<string>();
            }

            else
            {
                need_grad = new HashSet<string>(arg_names.Except(input_shapes.Keys));
            }

            var grad_req = arg_names.ToDictionary(name => name, v => need_grad.Contains(v) ? OpReqType.KWriteTo : OpReqType.KNullOp);

            List<NDArray> arg_arrays = new List<NDArray>();

            //create or borrow arguments and gradients
            for (int i = 0; i < arg_names.Count; i++)
            {
                var name = arg_names[i];
                if (!param_names.Contains(name))
                {
                    NDArray arg_arr = null;
                    //data or label
                    if (shared_data_arrays != null && shared_data_arrays.ContainsKey(name))
                    {
                        arg_arr = shared_data_arrays[name];

                        if (Util.prod(arg_arr.GetShape()) >= Util.prod(arg_shapes[i]))
                        {
                            Util.assert(arg_types[i] == arg_arr.GetDtype());

                            arg_arr = arg_arr.Reshape(new Shape(arg_shapes[i]));

                        }
                        else
                        {
                            logger.Warn($"bucketing: data \"{name}\" has a shape {new Shape(arg_shapes[i])}" +
                                        ", which is larger than already allocated " +
                                        $"shape {arg_arr.GetShape()}" +
                                        ". Need to re-allocate. Consider putting " +
                                        "default_bucket_key to be the bucket taking the largest " +
                                        "input for better memory sharing.");
                            arg_arr = NDArray.Zeros(new Shape(arg_shapes[i]), ctx, dtype: arg_types[i]);

                            // replace existing shared array because the new one is bigger
                            shared_data_arrays[name] = arg_arr;
                        }

                    }
                    else
                    {
                        arg_arr = NDArray.Zeros(new Shape(arg_shapes[i]), ctx, dtype: arg_types[i]);
                        if (shared_data_arrays != null)
                        {
                            shared_data_arrays[name] = arg_arr;
                        }

                    }

                    arg_arrays.Add(arg_arr);

                }
                else
                {
                    NDArray arg_arr = null;
                    if (base_exec == null)
                    {
                        arg_arr = NDArray.Zeros(new Shape(arg_shapes[i]), ctx, dtype: arg_types[i]);

                        if (need_grad_input && need_grad.Contains(name))
                        {
                            var grad_arr = NDArray.Zeros(new Shape(arg_shapes[i]), ctx, dtype: arg_types[i]);
                            grad_arrays[name] = grad_arr;
                        }

                    }
                    else
                    {
                        arg_arr = base_exec.arg_dict[name];
                        Util.assert(arg_arr.GetShape() == new Shape(arg_shapes[i]));
                        Util.assert(arg_arr.GetDtype() == arg_types[i]);
                        if (need_grad_input && need_grad.Contains(name))
                        {
                            grad_arrays[name] = base_exec.grad_dict[name];
                        }
                    }
                    arg_arrays.Add(arg_arr);
                }


            }
            List<NDArray> aux_arrays;
            if (base_exec == null)
            {
                aux_arrays = aux_shapes.Zip(aux_type, (l, r) => NDArray.Zeros(new Shape(l), ctx, r)).ToList();
            }
            else
            {
                for (int i = 0; i < base_exec.aux_arrays.Count; i++)
                {
                    var a = base_exec.aux_arrays[i];
                    Util.assert((new Shape(aux_shapes[i])) == a.GetShape());
                    Util.assert(aux_type[i] == a.GetDtype());
                }
                aux_arrays = base_exec.aux_arrays;
            }
     

            var executor = sym.Bind(ctx, arg_arrays, grad_arrays,
                   grad_req, aux_arrays, null, base_exec);
            return executor;
        }

        public void load_data_batch(IDataBatch data_batch)
        {
            _load_data(data_batch, this.data_arrays);
            _load_label(data_batch, this.label_arrays);
        }

        /// <summary>
        /// Perform a forward pass on each executor
        /// </summary>
        /// <param name="is_train"></param>
        public void forward(bool is_train)
        {
            foreach (var texec in train_execs)
            {
                texec.Forward(is_train: is_train);
            }


        }
        /// <summary>
        /// Perform a backward pass on each executor
        /// </summary>
        public void backward()
        {
            foreach (var texec in train_execs)
            {
                texec.Backward();
            }
        }
        public void update_metric(EvalMetric metric, List<NDArray> labels)
        {
            for (int index = 0; index < train_execs.Count; index++)
            {
                var texec = train_execs[index];
                var islice = slices[index];
                var labels_slice = labels.Select(s => s.Slice((uint) islice.Item1, (uint) islice.Item2)).ToList();
                metric.update(labels_slice, texec.outputs);
            }
        }

        private static void _load_general(List<NDArray> data, List<List<Tuple<Tuple<int, int>, NDArray>>> targets)
        {

            for (int i = 0; i < data.Count; i++)
            {
                var d_src = data[i];
                foreach (var dst in targets[i])
                {
                
                    var slice_idx = dst.Item1;
                    var d_dst = dst.Item2;
                    d_src.Slice((uint) slice_idx.Item1, (uint) slice_idx.Item2).CopyTo(d_dst);
                }
              
            }

        }

        private static void _load_data(IDataBatch batch, List<List<Tuple<Tuple<int, int>, NDArray>>> targets)
        {
            _load_general(batch.data, targets);
        }

        private static void _load_label(IDataBatch batch, List<List<Tuple<Tuple<int, int>, NDArray>>> targets)
        {
            _load_general(batch.label, targets);
        }


  
    }
}
