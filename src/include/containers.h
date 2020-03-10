#pragma once

#pragma once
#include <deque>
#include <assert.h>
#include <debugapi.h>
#include <vector>
#include <condition_variable>
#include <queue>
#include <atomic>
#include "../exceptions.h"

// Effective container for storing in-place, cache-local objects - deleting does not shift, dead objects can be reused later
//  and references to live objects are valid forever.
// We risk fragmentation after long use but let's have this as an experiment how to reduce the number of dynamic allocations
//  to minimum.
// Iterator and foreach support - skips over dead elements 
template <typename T>
concept Invalidable = requires(T t) { { t.IsInvalid() } -> bool; { t.Invalidate() }; { T::Invalid() }->T; };

template <Invalidable TElement>
class ValueContainer
{
	using Container = std::deque<TElement>;  // As long we grow/shrink on start/end, element references are valid forever

	// Various ways to get to IsInvalid - value type vs. pointer type
	template <typename TArgument, std::enable_if_t<std::is_class<TArgument>::value, int> = 0>
	static bool IsInvalid(TArgument val) { return val.IsInvalid(); }
	//template <typename TArgument, std::enable_if_t<std::is_reference<TArgument>::value, int> = 0>
	//static bool IsInvalid(TArgument val) { return val.IsInvalid(); }
	//template <typename TArgument, std::enable_if_t<std::is_pointer<TArgument>::value, int> = 0>
	//static bool IsInvalid(TArgument val) { return val->IsInvalid(); }
public:
	class iterator
	{
		Container* container;
		int index;
	public:
		iterator(Container& container, int index) : container(&container), index(index) {}
		typename TElement& operator*() const { return (*container)[index]; }
		bool operator!= (iterator other) { return index != other.index; }
		iterator operator++() // prefix increment
		{	// Advance over dead elements
			while (++index < container->size() && ValueContainer::IsInvalid((*container)[index])) {};
			return *this;
		}
	};

	Container container;
public:
	/* Size and lookup */
	auto Size() { return container.size(); };
	TElement& operator[](int index) { return container[index]; } 

	/* In-place forwarding construction avoiding any copy. Must use if you want to use [this] in your constructor or if your item in non-copyable */
	template <typename... ConstructionArgs>
	TElement& ConstructElement(ConstructionArgs&&... args)
	{
		auto dead_item = std::find_if(container.begin(), container.end(), [this](auto val) { return IsInvalid(val); });

		/* Find if we can insert into already allocated space */
		if (dead_item != container.end()) {
			/* Manually destroy old and in-place construct new */
			(*dead_item).~TElement();
			new (&*dead_item) TElement(std::forward<ConstructionArgs>(args)...);
			return *dead_item;
		}

		/* If not, we need to grow with an invalid item, then in-place construct it */
		TElement& new_alloc = container.emplace_back(std::forward<ConstructionArgs>(args)...);
		return new_alloc;
	}
	/* Copying construction */
	TElement& Add(TElement item)
	{
		/* Place over a dead item by assignment if such dead item exists. Otherwise append to back. */
		auto dead_item = std::find_if(container.begin(), container.end(), [this](auto val) { return IsInvalid(val); });
		if (dead_item != container.end()) {
			*dead_item = item;
			return *dead_item;
		}
		return container.emplace_back(item);
	}
	void Remove(TElement& item)
	{	// O(n) search is not really needed here. Just flag it destroyed and it will be recycled later, maybe.
		item.Invalidate();
	}
	void Shrink()
	{	// Slice out dead objects on the beginning and end of deque. Don't invalidate references.
		auto size_before = container.size();
	    while (!container.empty() && ValueContainer::IsInvalid(container.front()))
			container.pop_front();
		while (!container.empty() && ValueContainer::IsInvalid(container.back()))
			container.pop_back();

		if (container.size() != size_before && size_before >= 50) {
			char buff[50];
			std::sprintf(buff, "Shrunk %zd items, now size: %zd\r\n", size_before - container.size(), container.size());
			OutputDebugString(buff);
		}
	}

	iterator begin()
	{	
		auto it = iterator(container, 0);
		// Skip also dead elements at the start
		while (it != end() && ValueContainer::IsInvalid(*it))
			++it;
		return it;
	}
	iterator end() { return iterator(container, container.size()); }
};


template <typename TValue>
class GrowingDeque
{
	using Container = std::vector<TValue>;
	Container cont;
	int front_index = 0;
public:
	GrowingDeque(int reservedCapacity = 0)
	{
		if (reservedCapacity)
			cont.reserve(reservedCapacity);
	}
	
	TValue pop_front()
	{
		assert(front_index < size());
		TValue val = cont[front_index];
		++front_index;
		return val;
	}

	size_t size() { return cont.size(); }
	void push_back(TValue val) { cont.push_back(val); }

	template <class... TArgs>
	void emplace_back(TArgs&&... args)
	{
		cont.emplace_back(std::forward<TArgs>(args)...);
	}
};

/*
 * blockingqueue.h
 *
 *  Created on: May 20, 2016
 *      Author: bravegag
 */

template<class Job>
class blocking_queue {
private:
	std::queue<Job> queue_;
	mutable std::mutex mutex_;
	std::condition_variable not_empty_cond_;
	std::condition_variable not_full_cond_;
	int capacity_ = 0;
	std::atomic<bool> finished = false;
public:
	blocking_queue(int capacity) : capacity_(capacity) {

	}

	void put(const Job& job) {
		std::unique_lock<std::mutex> lock(mutex_);
		assert(!finished);
		if (finished)
			throw GameException("Use after finished");
		while (queue_.size() >= capacity_) {
			not_full_cond_.wait(lock, [&]() { return (queue_.size() < capacity_); });
		}
		queue_.push(job);
		not_empty_cond_.notify_one();
		assert(!queue_.empty());
	}

	bool take(Job& job) {
		std::unique_lock<std::mutex> lock(mutex_);
		while (queue_.empty() && !finished) {
			not_empty_cond_.wait(lock, [&]() { return !queue_.empty() || finished; });
		}
		if (finished && queue_.empty())
			return false;
		
		job = queue_.front();
		queue_.pop();
		not_full_cond_.notify_one();
		assert(queue_.size() < capacity_);
		return true;
	}

	void finish() {
		std::unique_lock<std::mutex> lock(mutex_);
		assert(!finished);
		finished = true;
		not_empty_cond_.notify_all();
	}

	int size() const {
		std::unique_lock<std::mutex> lock(mutex_);
		return queue_.size();
	}
};