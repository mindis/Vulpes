﻿// The MIT License (MIT)
// 
// Copyright (c) 2014 SpiegelSoft Ltd
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
namespace DeepBelief

module Kernels = 

    open Microsoft.FSharp.Quotations
    open Alea.CUDA
    open Alea.CUDA.Utilities

    type MatrixMulKernelSignature = deviceptr<float32> -> deviceptr<float32> -> deviceptr<float32> -> int -> int -> int -> int -> unit

    // a quotation that: fun height width blockIndex -> begin, end, step
    type IterationStrategy = Expr<int -> int -> int -> int * int * int>

    // a quotation that: fun A B ty k tx -> result
    type MultiplyElement = Expr<float32[,] -> float32[,] -> int -> int -> int -> float32>

    type CUpdate = Expr<int -> int -> int -> int -> int -> int -> int -> int>

    type Strategy =
        {
            BlockSize : int
            AIteration : IterationStrategy
            BIteration : IterationStrategy
            MultiplyElement : MultiplyElement
            CUpdate : CUpdate
        }

    let multiplyStrategy blockSize =
        let aIteration = 
            <@ fun height width blockIndex ->
                let b = width * blockSize * blockIndex
                let e = width * blockSize * blockIndex + width - 1
                let s = blockSize
                b, e, s @>

        let bIteration =
            <@ fun height width blockIndex ->
                let b = blockSize * blockIndex
                let e = blockSize * blockIndex + width * (height - blockSize)
                let s = blockSize * width
                b, e, s @>

        let multiplyElement = <@ fun (A:float32[,]) (B:float32[,]) ty k tx -> A.[ty, k] * B.[k, tx] @>
        let cUpdate = <@ fun hB wB blockSize yBlockIndex yThreadIndex xBlockIndex xThreadIndex -> wB * (blockSize * yBlockIndex + yThreadIndex) + (blockSize * xBlockIndex + xThreadIndex) @>
        {
            BlockSize = blockSize
            AIteration = aIteration
            BIteration = bIteration
            MultiplyElement = multiplyElement
            CUpdate = cUpdate
        }

    let multiplyByTransposeStrategy blockSize =
        let aIteration = 
            <@ fun height width blockIndex ->
                let b = width * blockSize * blockIndex
                let e = width * blockSize * blockIndex + width - 1
                let s = blockSize
                b, e, s @>

        let bIteration =
            <@ fun height width blockIndex ->
                let b = width * blockSize * blockIndex
                let e = width * blockSize * blockIndex + width - 1
                let s = blockSize
                b, e, s @>

        let multiplyElement = <@ fun (A:float32[,]) (B:float32[,]) ty k tx -> A.[ty, k] * B.[tx, k] @>
        let cUpdate = <@ fun hB wB blockSize yBlockIndex yThreadIndex xBlockIndex xThreadIndex -> hB * (blockSize * yBlockIndex + yThreadIndex) + (blockSize * xBlockIndex + xThreadIndex) @>
        {
            BlockSize = blockSize
            AIteration = aIteration
            BIteration = bIteration
            MultiplyElement = multiplyElement
            CUpdate = cUpdate
        }

    let transposeAndMultiplyStrategy blockSize =
        let aIteration = 
            <@ fun height width blockIndex ->
                let b = blockSize * blockIndex
                let e = blockSize * blockIndex + width * (height - blockSize)
                let s = blockSize * width
                b, e, s @>

        let bIteration =
            <@ fun height width blockIndex ->
                let b = blockSize * blockIndex
                let e = blockSize * blockIndex + width * (height - blockSize)
                let s = blockSize * width
                b, e, s @>

        let multiplyElement = <@ fun (A:float32[,]) (B:float32[,]) ty k tx -> A.[k, ty] * B.[k, tx] @>
        let cUpdate = <@ fun hB wB blockSize yBlockIndex yThreadIndex xBlockIndex xThreadIndex -> wB * (blockSize * yBlockIndex + yThreadIndex) + (blockSize * xBlockIndex + xThreadIndex) @>
        {
            BlockSize = blockSize
            AIteration = aIteration
            BIteration = bIteration
            MultiplyElement = multiplyElement
            CUpdate = cUpdate
        }

    let matrixMulKernel (blockSize:int) (strategy:Strategy) =
        <@ fun (C:deviceptr<float32>) (A:deviceptr<float32>) (B:deviceptr<float32>) (hA:int) (wA:int) (hB:int) (wB:int) ->

            // Block index
            let bx = blockIdx.x
            let by = blockIdx.y

            // Thread index
            let tx = threadIdx.x
            let ty = threadIdx.y

            let aBegin, aEnd, aStep = (%strategy.AIteration) hA wA by
            let bBegin, bEnd, bStep = (%strategy.BIteration) hB wB bx

            // Csub is used to store the element of the block sub-matrix
            // that is computed by the thread
            let mutable Csub = 0.0f

            // Loop over all the sub-matrices of A and B
            // required to compute the block sub-matrix
            let mutable a = aBegin
            let mutable b = bBegin
            while a <= aEnd do
            
                // Declaration of the shared memory array As used to
                // store the sub-matrix of A
                let As = __shared__.Array2D(blockSize, blockSize)

                // Declaration of the shared memory array Bs used to
                // store the sub-matrix of B
                let Bs = __shared__.Array2D(blockSize, blockSize)

                // Load the matrices from device memory
                // to shared memory; each thread loads
                // one element of each matrix
                As.[ty, tx] <- A.[a + wA * ty + tx]
                Bs.[ty, tx] <- B.[b + wB * ty + tx]

                // Synchronize to make sure the matrices are loaded
                __syncthreads()

                // Multiply the two matrices together;
                // each thread computes one element
                // of the block sub-matrix
                for k = 0 to blockSize - 1 do
                    Csub <- Csub + ((%strategy.MultiplyElement) As Bs ty k tx)

                // Synchronize to make sure that the preceding
                // computation is done before loading two new
                // sub-matrices of A and B in the next iteration
                __syncthreads()

                a <- a + aStep
                b <- b + bStep

            // Write the block sub-matrix to device memory;
            // each thread writes one element
            C.[(%strategy.CUpdate) hB wB blockSize by ty bx tx] <- Csub @>

    let multiplyVectorByMatrixKernel (blockSize:int) =
        <@ fun (y:deviceptr<float32>) (A:deviceptr<float32>) (x:deviceptr<float32>) (hA:int) (wA:int) ->

            let Xds = __shared__.Array(blockSize);

            let bx = blockIdx.x
            let tx = threadIdx.x

            let row = bx * blockSize + tx;

            let mutable value = 0.0f

            let mutable m = 0
            let upperBound = (wA - 1)/blockSize
            for m in 0..upperBound do
                
                Xds.[tx] <- if (m * blockSize + tx < wA) then x.[m * blockSize + tx] else 0.0f
                __syncthreads()
                
                for k in 0..(blockSize - 1) do
                    value <- value + (if row < hA && m * blockSize + k < wA then A.[row * wA + m * blockSize + k] * Xds.[k] else 0.0f)
                __syncthreads()

            y.[row] <- value
            __syncthreads() @>

    let multiplyVectorByTransposeOfMatrixKernel (blockSize:int) =
        <@ fun (y:deviceptr<float32>) (A:deviceptr<float32>) (x:deviceptr<float32>) (hA:int) (wA:int) ->
            let Xds = __shared__.Array(blockSize);

            // Block index
            let bx = blockIdx.x

            // Thread index
            let tx = threadIdx.x

            let column = bx * blockSize + tx;

            let mutable value = 0.0f

            let mutable m = 0
            let upperBound = (hA - 1)/blockSize
            for m in 0..upperBound do

                if (m * blockSize + tx < hA) then
                    Xds.[tx] <-  x.[m * blockSize + tx]
                    __syncthreads()
                
                    for k in 0..(blockSize - 1) do
                        value <- value + (if column < wA && m * blockSize + k < hA then A.[column + wA * (m * blockSize + k)] * Xds.[k] else 0.0f)
                    __syncthreads()

            if column < wA then y.[column] <- value
            __syncthreads() 
        @>

    [<ReflectedDefinition>]
    let jumpAhead (numThreads:int) (threadRank:int)
                  (stateStart:deviceptr<uint32>) (jumpAheadMatrices:deviceptr<uint32>)
                  (jumpAheadMatrixCache:deviceptr<uint32>) (state:deviceptr<uint32>) =

        let numThreadsPerBlock = blockDim.x * blockDim.y * blockDim.z
        let threadRankInBlock = threadIdx.z * blockDim.x * blockDim.y + threadIdx.y * blockDim.x + threadIdx.x

        let mutable p = 0
        while (1 <<< p) < numThreads do p <- p + 1
        let matrixSize = 256 * 8
        let mutable matrix = jumpAheadMatrices + (32 - p) * matrixSize

        // init rng state to start state
        for i = 0 to 7 do state.[i] <- stateStart.[i]

        for i = 0 to p - 1 do
            let statePrev = __local__.Array<uint32>(8)
            for j = 0 to 7 do statePrev.[j] <- state.[j]

            for j = 0 to 7 do
                let mutable stateWord = 0u

                for k = 0 to 31 do
                    __syncthreads()
                    let mutable l = threadRankInBlock
                    while l < 8 do
                        jumpAheadMatrixCache.[l] <- matrix.[l]
                        l <- l + numThreadsPerBlock
                    matrix <- matrix + 8

                    __syncthreads()
                    if (threadRank &&& (1 <<< i) <> 0) then
                        let mutable partialSums = 0u
                        for l = 0 to 7 do
                            partialSums <- partialSums ^^^ (jumpAheadMatrixCache.[l] &&& statePrev.[l])

                        let sum = partialSums
                        let sum = (sum >>> 16) ^^^ (sum &&& 0xffffu)
                        let sum = (sum >>> 8) ^^^ (sum &&& 0xffu)
                        let sum = (sum >>> 4) ^^^ (sum &&& 0xfu)
                        let sum = (sum >>> 2) ^^^ (sum &&& 0x3u)
                        let sum = (sum >>> 1) ^^^ (sum &&& 0x1u)

                        stateWord <- stateWord <<< 1
                        stateWord <- stateWord ||| sum

                if (threadRank &&& (1 <<< i) <> 0) then state.[j] <- stateWord

    // In this algorithm, acturally the struct is not used to carry data
    // from host to device, and we just reinterpret part of our shared
    // memory into a struct to operate easier. So we could use ref class
    // here. The RefClass attribute is an attribute builder in Alea.CUDA.Utilites,
    // it can automatically lookup all properties that is marked as 
    // RefClassField (or its child class RefClassArrayField), and build the
    // underlying data storage struct. But inside kernel, all ref class
    // are used as a pointer to the underlying struct, so you should be
    // careful, like when you need know the size of it, see below for more
    // detail.
    [<Class>]
    type XorShift7() =

        // with this mark, the builder will build an array ref class
        [<ClassEmbeddedArrayField(8)>]
        member this.States : uint32[] = failwith "device only"

        // with this mark, the builder will build property get/set handles
        [<ClassField>]
        member this.Index 
            with get () : int = failwith "device only"
            and set (value:int) : unit = failwith "device only"

        // Important here, we should use __sizeofclass instead of __sizeof
        // because now XorShift7 is ref class, so in ir, it is represented
        // by pointer, so if you use __sizeof, you get 4 or 8 (on 64bit),
        // that is just size of pointer! Use __sizeofclass to get the
        // content size!
        [<ReflectedDefinition>]
        static member Size = __sizeof<XorShift7>()

        // Bless is reinterpret a buffer into our ref class type, then use
        // __ptrtoobj to construct the ref class object, which will use
        // the memory pointed by the pointer as the underlying storage.
        [<ReflectedDefinition>]
        static member Bless(buffer:deviceptr<byte>) =
            buffer.Reinterpret<XorShift7>() |> __ptr_to_obj

        // Now, you can code method, and this pointer because an ir pointer
        // points to the underlying storage. You can use __debug(_) to show
        // what it is like in ir.
        [<ReflectedDefinition>]
        member this.Init(buffer:deviceptr<uint32>) =
            //__debug(this) // will print out the underlying ir value
            for i = 0 to 7 do this.States.[i] <- buffer.[i]
            this.Index <- 0

        // With this pointer, you now got a real ref struct, so you don't 
        // bother using xxxx.contents.xx stuff. Those are used in value
        // type struct, because struct is value type, so you cannot add
        // reflected definition on its member method cause that doesn't 
        // have a ref this pointer!
        // Also, ref class allows to be generic! This is quite powerful
        // But the bad thing is ref class is not suit for transfer data
        // from host to device. Although Alea.cuBase provides 
        // To/From Unmanaged Marshaller build framework, But that is
        // very slow. So the data that will be exchanged between host and
        // device should better be declared as value class, but once it is
        // in device, we can eaisly reinterpret and recreate ref class object
        // by functions like __ptrtoobj. value class struct is unmanaged
        // so it don't need to be reinterpret before it is send into device,
        // but struct cannot be generic. To make generic usage on struct,
        // I think we should look for Type Provider feature of F#.
        [<ReflectedDefinition>]
        member this.Next() =
            let mutable r = 0u
            let mutable t = 0u
            let index = this.Index

            t <- this.States.[(index + 7) &&& 0x7]
            t <- t ^^^ (t <<< 13)
            r <- t ^^^ (t <<< 9)
            t <- this.States.[(index + 4) &&& 0x7]
            r <- r ^^^ (t ^^^ (t <<< 7))
            t <- this.States.[(index + 3) &&& 0x7]
            r <- r ^^^ (t ^^^ (t >>> 3))
            t <- this.States.[(index + 1) &&& 0x7]
            r <- r ^^^ (t ^^^ (t >>> 10))
            t <- this.States.[index]
            t <- t ^^^ (t >>> 7)
            r <- r ^^^ (t ^^^ (t <<< 24))
            this.States.[index] <- r
            this.Index <- (index + 1) &&& 0x7

            r

    type XorShift7Signature<'T> = int -> int -> deviceptr<uint32> -> deviceptr<uint32> -> int -> deviceptr<'T> -> unit

    /// We generate the random numbers in different runs and in each block in non-overlapping streams.
    /// This template produces a single run, of total numRun runs, identified by the run rank.
    /// The streams are used to parallelize the generation of a run and must be a multiple of the block size.
    /// The total number of random variates generated in a run is numStreams*numSteps. 
    /// The concept of runs can also be used to generate nonoverlapping blocks of random numbers on multiple 
    /// devices, e.g. by mapping the number of devices to the numRuns and the run rank to the device id. 
    /// Obviously we can also implement the use cases of multiple runs per device, just let numRuns be a suitable
    /// multiple of the number of devices.
    let xorShiftKernel (convertExpr:Expr<uint32 -> 'T>) =
        <@ fun (numRuns:int) (runRank:int) (stateStart:deviceptr<uint32>) (jumpAheadMatrices:deviceptr<uint32>) (numSteps:int) (results:deviceptr<'T>) ->
            // Shared memory declaration; aligned to 4 because of the
            // intended usage as the cache for current row of the jump-ahead
            // matrix. 
            let sharedData = __shared__.Extern<byte>(4)

            // Calculate ranks of the block within the grid, and the thread
            // within the block, as well as rank of the thread within the
            // whole grid.
            let numBlocks = gridDim.x * gridDim.y * gridDim.z
            let blockRank = blockIdx.z * (gridDim.x * gridDim.y) + blockIdx.y * gridDim.x + blockIdx.x
            let numThreadsPerBlock = blockDim.x * blockDim.y * blockDim.z
            let numThreads = numBlocks * numThreadsPerBlock
            let threadRankInBlock = threadIdx.z * (blockDim.x * blockDim.y) + threadIdx.y * blockDim.x + threadIdx.x
            let threadRank = blockRank * numThreadsPerBlock + threadRankInBlock

            // Perform jumping ahead, taking into account the total number
            // of thread on all devices, as well as current thread rank
            // within all thread on all devices.
            let state = __local__.Array<uint32>(8) |> __array_to_ptr
            jumpAhead (numRuns * numThreads) (runRank * numThreads + threadRank) stateStart jumpAheadMatrices (sharedData.Reinterpret<uint32>()) state

            // Use corresponding piece of shared memory for xorshift7 RNG
            // data structure, and intialized RNG with the state calculated
            // through above jump-ahead procedure.
            let rng = XorShift7.Bless(sharedData + threadRankInBlock * XorShift7.Size)
            rng.Init(state)

            let mutable index = threadRank
            for i = 0 to numSteps - 1 do
                results.[index] <- (%convertExpr) (rng.Next())
                index <- index + numThreads @>

    let [<ReflectedDefinition>] sigmoid x = 1.0f / (1.0f + exp(-x))
    let [<ReflectedDefinition>] dSigmoid s x = s * (1.0f - s)

    let [<ReflectedDefinition>] pointwiseAdd (a : float32) (b : float32) = a + b
    let [<ReflectedDefinition>] pointwiseSubtract (a : float32) (b : float32) = a - b
    let [<ReflectedDefinition>] pointwiseMultiply (a : float32) (b : float32) = a * b

    type PointwiseOperation = Expr<float32 -> float32 -> float32>
    type ActivationFunction = Expr<float32 -> float32>
    type TransformationFunction = Expr<float32 -> float32>
    type SecondOrderTransformationFunction = Expr<float32 -> float32 -> float32>

    let activateKernel (blockSize : int) (activationFunction : ActivationFunction) =
        let strategy = multiplyStrategy blockSize
        <@ fun (result : deviceptr<float32>) (A : deviceptr<float32>) (rnd : deviceptr<float32>) ->

            // Block index
            let bx = blockIdx.x

            // Thread index
            let tx = threadIdx.x

            let i = bx * blockSize + tx;
            result.[i] <- if (%activationFunction) A.[i] < rnd.[i] then 0.0f else 1.0f
            __syncthreads() @>

    let transformKernel (blockSize : int) (transformationFunction : TransformationFunction) =
        <@ fun (tX : deviceptr<float32>) (X : deviceptr<float32>) (startIndex : int) (size : int) ->

            // Block index
            let bx = blockIdx.x

            // Thread index
            let tx = threadIdx.x

            let i = bx * blockSize + tx;

            // Write the block sub-matrix to device memory;
            // each thread writes one element
            if i >= startIndex && i < startIndex + size then
                tX.[i] <- (%transformationFunction) X.[i]
            else
                tX.[i] <- 0.0f
            __syncthreads() @>

    let pointwiseBinaryOperationKernel (blockSize : int) (operation : PointwiseOperation) =
        <@ fun (result : deviceptr<float32>) (lhs : deviceptr<float32>) (rhs : deviceptr<float32>) ->

            // Block index
            let bx = blockIdx.x

            // Thread index
            let tx = threadIdx.x

            let i = bx * blockSize + tx;

            __syncthreads()
            // Write the block sub-matrix to device memory;
            // each thread writes one element
            result.[i] <- (%operation) lhs.[i] rhs.[i]
            __syncthreads() @>

    let coerceKernel (blockSize : int) =
        <@ fun (X : deviceptr<float32>) (minIndex : int) (maxIndex : int) (value : float32) ->

            let index = (blockIdx.x * blockSize + threadIdx.x)
            if index >= minIndex && index <= maxIndex then
                X.[index] <- value
                __syncthreads() @>

    let activateFirstRowKernel (blockSize:int) =
        <@ fun (M:deviceptr<float32>) (wM:int) (nActivations:int) -> 
            // Block index
            let bx = blockIdx.x
            // Thread index
            let tx = threadIdx.x

            let start = blockSize * bx
            let i = start + tx
            M.[i] <- if i < nActivations then 1.0f else 0.0f
            @>

    let activateFirstColumnKernel (blockSize:int) =
        <@ fun (M:deviceptr<float32>) (hM:int) (wM:int) (nActivations:int) -> 
            // Block index
            let bx = blockIdx.x
            // Thread index
            let tx = threadIdx.x

            let start = wM * blockSize * bx
            let i = start + wM * tx
            let max = nActivations * wM
            M.[i] <- if i < max then 1.0f else 0.0f
            @>

    let outerProductKernel (blockSize : int) =
        let strategy = multiplyStrategy blockSize
        <@ fun (A : deviceptr<float32>) (v : deviceptr<float32>) (w : deviceptr<float32>) (wA : int) ->
            
            let bx = blockIdx.x
            let by = blockIdx.y

            let tx = threadIdx.x
            let ty = threadIdx.y

            let i = bx * blockSize + tx;
            let j = by * blockSize + ty;

            A.[i * wA + j] <- v.[i] * w.[j]
            @>

    let scalarMultiplyMatrixKernel (blockSize : int) =
        let strategy = multiplyStrategy blockSize
        <@ fun (A : deviceptr<float32>) (lambda : float32) ->
            
            // Block index
            let bx = blockIdx.x

            // Thread index
            let tx = threadIdx.x

            let i = bx * blockSize + tx;

            // Write the block sub-matrix to device memory;
            // each thread writes one element
            A.[i] <- A.[i] * lambda
            __syncthreads() @>

    let multiplyVectorByMatrixAndTransformKernel (blockSize:int) (transformationFunction : TransformationFunction) =
        <@ fun (y:deviceptr<float32>) (A:deviceptr<float32>) (x:deviceptr<float32>) (hA:int) (wA:int) ->

            let Xds = __shared__.Array(blockSize);

            let bx = blockIdx.x
            let tx = threadIdx.x

            let row = bx * blockSize + tx;

            let mutable value = 0.0f

            let mutable m = 0
            let upperBound = (wA - 1)/blockSize
            for m in 0..upperBound do
                
                Xds.[tx] <- if (m * blockSize + tx < wA) then x.[m * blockSize + tx] else 0.0f
                __syncthreads()
                
                for k in 0..(blockSize - 1) do
                    value <- value + (if row < hA && m * blockSize + k < wA then A.[row * wA + m * blockSize + k] * Xds.[k] else 0.0f)
                __syncthreads()

            y.[row] <- (%transformationFunction) value
            __syncthreads() @>

    let multiplyVectorByMatrixAndTransformTwiceKernel (blockSize:int) (transformationFunction1 : TransformationFunction) (transformationFunction2 : SecondOrderTransformationFunction) =
        <@ fun (y2:deviceptr<float32>) (y1:deviceptr<float32>) (A:deviceptr<float32>) (x:deviceptr<float32>) (hA:int) (wA:int) ->

            let Xds = __shared__.Array(blockSize);

            let bx = blockIdx.x
            let tx = threadIdx.x

            let row = bx * blockSize + tx;

            let mutable value = 0.0f

            let mutable m = 0
            let upperBound = (wA - 1)/blockSize
            for m in 0..upperBound do
                
                Xds.[tx] <- if (m * blockSize + tx < wA) then x.[m * blockSize + tx] else 0.0f
                __syncthreads()
                
                for k in 0..(blockSize - 1) do
                    value <- value + (if row < hA && m * blockSize + k < wA then A.[row * wA + m * blockSize + k] * Xds.[k] else 0.0f)
                __syncthreads()

            y1.[row] <- (%transformationFunction1) value
            y2.[row] <- (%transformationFunction2) y1.[row] value
            __syncthreads() @>
