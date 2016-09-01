﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace mxnet.numerics.nbase
{

    public class NArray<T, TC, TOut>
        where T : new()
        where TC : ICalculator<T>, new()
        where TOut : NArray<T, TC, TOut> , new()
    {
        private static readonly TC Calculator = new TC();

        public Shape shape { get; protected set; }

        protected T[] storage;

        public T[] data => storage;

        public GCHandle GetDataGcHandle()
        {
          return  GCHandle.Alloc(storage, GCHandleType.Pinned);
        }

        public NArray()
        {
            
        }

        public NArray(Shape shape)
        {
            this.shape = new Shape(shape);
            storage = new T[this.shape.size];
        }
        public NArray(Shape shape, T[] data)
        {
            this.shape = new Shape(shape);
            storage = new T[this.shape.size];
            Array.Copy(data, storage, Math.Min(data.Length, storage.Length));
        }

        static void ArrayCopy(T[] src, T[] dst, int src_start, int src_end, int dst_start, int dst_end,
            Slice[] slice, uint[] src_dim,uint[] dst_dim
            )
        {
            var first_slice = slice.FirstOrDefault();
            if (first_slice != null)
            {
                int src_pad = CalcPad(src_dim.Skip(1).ToArray());
                var dst_index = 0;
                var dst_pad = CalcPad(dst_dim.Skip(1).ToArray());
                for (var i = first_slice.start; i < first_slice.stop; i++)
                {
                    ArrayCopy(src, dst,
                        src_start + i*src_pad,
                        src_start + (i + 1)*src_pad,
                        dst_start + dst_index*dst_pad,
                        dst_start + (dst_index + 1)*dst_pad,
                        slice.Skip(1).ToArray(),
                        src_dim.Skip(1).ToArray(),
                        dst_dim.Skip(1).ToArray());
                    dst_index++;
                }
            }
            else
            {
                Array.Copy(src, src_start, dst, dst_start, dst_end - dst_start);
            }
        }

        private static int CalcPad(uint[] src_dim)
        {
            return (int)src_dim.Aggregate((long)1, (l, r) => l * r);
        }

        public TOut this[params Slice[] slice]
        {
            get
            {
                var src_dim = shape.data;
                var tslice = slice.Select((x, i) => x.Translate(src_dim[i])).ToArray();
                var dst_dim_temp = tslice.Select(s => (uint) s.size).ToArray();
                var dst_dim = (uint[])src_dim.Clone();
                Array.Copy(dst_dim_temp, dst_dim, dst_dim_temp.Length);

                var ret = new TOut();
                ret.shape = new Shape(dst_dim);
                ret.storage = new T[ret.shape.size];

                ArrayCopy(storage, ret.storage, 0, storage.Length, 0, ret.storage.Length, tslice, shape.data, dst_dim);

                return ret;
            }
        }

        public TOut this[int d0]
        {
            get
            {
                var ret = new TOut();
                ret.shape = new Shape(shape.data.Skip(1).ToArray());
                ret.storage = new T[ret.shape.size];

                int retindex = 0;
                int d1 =(int) shape.data[1];
                for (int i = 0; i < d1; i++)
                {
                    ret.storage[retindex] = storage[d0 * d1 + i];
                    retindex++;
                }
                return ret;
            }
        }

        public TOut Flat()
        {
            var ret = new TOut();
            ret.shape = new Shape(shape.size);
            ret.storage = storage.ToArray();
            return ret;
        }

        public TOut Compare(NArray<T, TC, TOut> other)
        {
            TOut ret = new TOut
            {
                shape = shape,
                storage = this.storage
                    .Select((x, i) => Calculator.Compare(x, other.storage[i])).ToArray()
  
            };
            return ret;
        }
        #region

        public T Sum()
        {
           return Calculator.Sum( this.storage);
        }
        public int Argmax()
        {
            return Calculator.Argmax(this.storage);
        }

        

        #endregion
    }
}
